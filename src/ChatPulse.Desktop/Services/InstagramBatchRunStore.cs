using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Temporary drafts exist only during a run. A completed run replaces the last successful
/// results atomically; stopped/failed drafts are discarded without keeping history.
/// </summary>
public sealed class InstagramBatchRunStore(string databasePath)
{
    private const string CREATE_DRAFTS = """
        CREATE TABLE IF NOT EXISTS InstagramBatchCheckDrafts (
            RunId TEXT NOT NULL,
            TargetUserName TEXT NOT NULL COLLATE NOCASE,
            ParticipantUserName TEXT NOT NULL COLLATE NOCASE,
            Direction TEXT NOT NULL CHECK (Direction IN ('followers','following')),
            BrowserUserName TEXT NOT NULL,
            SearchStatus TEXT NOT NULL CHECK (SearchStatus IN ('FOUND','NOT_FOUND','INCONCLUSIVE')),
            Details TEXT NOT NULL,
            CheckedAtUtc TEXT NOT NULL,
            PRIMARY KEY (RunId, TargetUserName, ParticipantUserName, Direction)
        );
        """;
    private const string CREATE_CHECKS = """
        CREATE TABLE IF NOT EXISTS InstagramSubscriptionChecks (
            TargetUserName TEXT NOT NULL COLLATE NOCASE,
            ParticipantUserName TEXT NOT NULL COLLATE NOCASE,
            Direction TEXT NOT NULL CHECK (Direction IN ('followers','following')),
            BrowserUserName TEXT NOT NULL,
            SearchStatus TEXT NOT NULL CHECK (SearchStatus IN ('FOUND','NOT_FOUND','INCONCLUSIVE')),
            Details TEXT NOT NULL,
            CheckedAtUtc TEXT NOT NULL,
            PRIMARY KEY (TargetUserName, ParticipantUserName, Direction)
        );
        """;
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Remove drafts left by a previous crash before starting a new check.</summary>
    public void DiscardAbandonedDrafts(string targetUserName)
    {
        using var connection = Open();
        Execute(connection, null, CREATE_DRAFTS);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM InstagramBatchCheckDrafts WHERE TargetUserName=$target";
        command.Parameters.AddWithValue("$target", targetUserName.Trim());
        command.ExecuteNonQuery();
    }

    public void Stage(string runId, string targetUserName, string browserUserName, InstagramBatchProgress progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = Open();
        Execute(connection, null, CREATE_DRAFTS);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO InstagramBatchCheckDrafts
                (RunId, TargetUserName, ParticipantUserName, Direction, BrowserUserName,
                 SearchStatus, Details, CheckedAtUtc)
            VALUES ($run, $target, $user, $direction, $browser, $status, $details, $at)
            ON CONFLICT(RunId, TargetUserName, ParticipantUserName, Direction) DO UPDATE SET
                BrowserUserName=excluded.BrowserUserName,
                SearchStatus=excluded.SearchStatus,
                Details=excluded.Details,
                CheckedAtUtc=excluded.CheckedAtUtc
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$target", targetUserName.Trim());
        command.Parameters.AddWithValue("$user", progress.UserName);
        command.Parameters.AddWithValue("$direction", progress.Kind);
        command.Parameters.AddWithValue("$browser", browserUserName);
        command.Parameters.AddWithValue("$status", progress.Result.Status.ToString());
        command.Parameters.AddWithValue("$details", progress.Result.Details);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void PublishCompleted(string runId, string targetUserName, InstagramBatchResultSummary summary)
    {
        if (!InstagramCheckDirectionSelection.IsValid(summary.Directions) ||
            summary.Outcome != InstagramBatchOutcome.COMPLETED ||
            summary.CompletedSearches != summary.TotalSearches || summary.TotalSearches <= 0 ||
            summary.Errors != 0 || summary.Found + summary.NotFound != summary.TotalSearches ||
            !summary.HasNotFoundBreakdown)
            throw new InvalidOperationException("Нельзя публиковать проверку с ошибками или неполными результатами.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, CREATE_DRAFTS);
        Execute(connection, transaction, CREATE_CHECKS);
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN SearchStatus='FOUND' THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN SearchStatus='NOT_FOUND' THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN SearchStatus='INCONCLUSIVE' THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN SearchStatus='NOT_FOUND' AND Direction='followers'
                                         THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN SearchStatus='NOT_FOUND' AND Direction='following'
                                         THEN 1 ELSE 0 END), 0)
                FROM InstagramBatchCheckDrafts
                WHERE RunId=$run AND TargetUserName=$target
                """;
            count.Parameters.AddWithValue("$run", runId);
            count.Parameters.AddWithValue("$target", targetUserName.Trim());
            using var reader = count.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != summary.TotalSearches ||
                reader.GetInt64(1) != summary.Found || reader.GetInt64(2) != summary.NotFound ||
                reader.GetInt64(3) != summary.Errors ||
                reader.GetInt64(4) != summary.NotFoundFollowers.GetValueOrDefault(-1) ||
                reader.GetInt64(5) != summary.NotFoundFollowing.GetValueOrDefault(-1))
                throw new InvalidOperationException("Сохранённые результаты не совпали с итоговой статистикой проверки.");
        }
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM InstagramSubscriptionChecks
                WHERE TargetUserName=$target
                  AND ((Direction='followers' AND $followers=1) OR
                       (Direction='following' AND $following=1))
                """;
            delete.Parameters.AddWithValue("$target", targetUserName.Trim());
            delete.Parameters.AddWithValue("$followers", (summary.Directions & InstagramCheckDirections.FOLLOWERS) != 0 ? 1 : 0);
            delete.Parameters.AddWithValue("$following", (summary.Directions & InstagramCheckDirections.FOLLOWING) != 0 ? 1 : 0);
            delete.ExecuteNonQuery();
        }
        using (var promote = connection.CreateCommand())
        {
            promote.Transaction = transaction;
            promote.CommandText = """
                INSERT INTO InstagramSubscriptionChecks
                    (TargetUserName, ParticipantUserName, Direction, BrowserUserName,
                     SearchStatus, Details, CheckedAtUtc)
                SELECT TargetUserName, ParticipantUserName, Direction, BrowserUserName,
                       SearchStatus, Details, CheckedAtUtc
                FROM InstagramBatchCheckDrafts
                WHERE RunId=$run AND TargetUserName=$target
                """;
            promote.Parameters.AddWithValue("$run", runId);
            promote.Parameters.AddWithValue("$target", targetUserName.Trim());
            promote.ExecuteNonQuery();
        }
        InstagramBatchSummaryStore.SaveCompleted(connection, transaction, targetUserName, summary);
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM InstagramBatchCheckDrafts WHERE TargetUserName=$target";
            cleanup.Parameters.AddWithValue("$target", targetUserName.Trim());
            cleanup.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Publishes a single requested direction without changing other participants or
    /// the last completed bulk-check summary. An interrupted attempt is never published.
    /// </summary>
    public void PublishIndividual(string runId, string targetUserName, string userName,
        InstagramCheckDirections direction) => PublishIndividualDirections(runId, targetUserName,
            userName, userName, direction);

    /// <summary>Publishes the checked direction(s) for one participant atomically.</summary>
    public void PublishIndividualDirections(string runId, string targetUserName,
        string activityUserName, string subscriptionUserName, InstagramCheckDirections directions)
    {
        if (!InstagramCheckDirectionSelection.IsValid(directions))
            throw new ArgumentOutOfRangeException(nameof(directions));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, CREATE_DRAFTS);
        Execute(connection, transaction, CREATE_CHECKS);
        var selected = new List<(string Kind, string UserName)>();
        if ((directions & InstagramCheckDirections.FOLLOWERS) != 0)
            selected.Add(("followers", activityUserName));
        if ((directions & InstagramCheckDirections.FOLLOWING) != 0)
            selected.Add(("following", subscriptionUserName));

        foreach (var (kind, userName) in selected)
        {
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = """
                SELECT COUNT(*) FROM InstagramBatchCheckDrafts
                WHERE RunId=$run AND TargetUserName=$target AND ParticipantUserName=$user
                      AND Direction=$kind AND SearchStatus IN ('FOUND','NOT_FOUND')
                """;
            count.Parameters.AddWithValue("$run", runId);
            count.Parameters.AddWithValue("$target", targetUserName.Trim());
            count.Parameters.AddWithValue("$user", userName);
            count.Parameters.AddWithValue("$kind", kind);
            if (Convert.ToInt64(count.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Нет успешного результата выборочной проверки.");
        }
        foreach (var (kind, userName) in selected)
        {
            using var promote = connection.CreateCommand();
            promote.Transaction = transaction;
            promote.CommandText = """
                INSERT INTO InstagramSubscriptionChecks
                    (TargetUserName, ParticipantUserName, Direction, BrowserUserName,
                     SearchStatus, Details, CheckedAtUtc)
                SELECT TargetUserName, ParticipantUserName, Direction, BrowserUserName,
                       SearchStatus, Details, CheckedAtUtc
                FROM InstagramBatchCheckDrafts
                WHERE RunId=$run AND TargetUserName=$target AND ParticipantUserName=$user
                      AND Direction=$kind
                ON CONFLICT(TargetUserName, ParticipantUserName, Direction) DO UPDATE SET
                    BrowserUserName=excluded.BrowserUserName,
                    SearchStatus=excluded.SearchStatus,
                    Details=excluded.Details,
                    CheckedAtUtc=excluded.CheckedAtUtc
                """;
            promote.Parameters.AddWithValue("$run", runId);
            promote.Parameters.AddWithValue("$target", targetUserName.Trim());
            promote.Parameters.AddWithValue("$user", userName);
            promote.Parameters.AddWithValue("$kind", kind);
            promote.ExecuteNonQuery();
        }
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM InstagramBatchCheckDrafts WHERE TargetUserName=$target";
            cleanup.Parameters.AddWithValue("$target", targetUserName.Trim());
            cleanup.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Discard the interrupted attempt; only the last completed results are retained.</summary>
    public void RecordInterrupted(string runId, string targetUserName, InstagramBatchResultSummary summary)
    {
        if (summary.Outcome == InstagramBatchOutcome.COMPLETED)
            throw new ArgumentException("Завершённый запуск должен быть опубликован.", nameof(summary));
        using var connection = Open();
        Execute(connection, null, CREATE_DRAFTS);
        using var cleanup = connection.CreateCommand();
        cleanup.CommandText = """
            DELETE FROM InstagramBatchCheckDrafts
            WHERE RunId=$run AND TargetUserName=$target
            """;
        cleanup.Parameters.AddWithValue("$run", runId);
        cleanup.Parameters.AddWithValue("$target", targetUserName.Trim());
        cleanup.ExecuteNonQuery();
    }
}
