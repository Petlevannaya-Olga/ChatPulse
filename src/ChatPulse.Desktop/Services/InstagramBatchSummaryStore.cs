using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>Only successful completed runs can replace the last published summary.</summary>
public sealed class InstagramBatchSummaryStore(string databasePath)
{
    public void Save(string targetUserName, InstagramBatchResultSummary summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserName);
        if (summary.Outcome != InstagramBatchOutcome.COMPLETED ||
            summary.CompletedSearches != summary.TotalSearches)
            throw new ArgumentException("Публиковать можно только полностью завершённую проверку.", nameof(summary));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        SaveCompleted(connection, transaction, targetUserName, summary);
        transaction.Commit();
    }

    internal static void SaveCompleted(SqliteConnection connection, SqliteTransaction transaction,
        string targetUserName, InstagramBatchResultSummary summary)
    {
        if (summary.Outcome != InstagramBatchOutcome.COMPLETED ||
            summary.CompletedSearches != summary.TotalSearches)
            throw new ArgumentException("Нельзя публиковать прерванный запуск.", nameof(summary));
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS InstagramBatchSummaries (
                    TargetUserName TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    FinishedAtUtc TEXT NOT NULL,
                    Outcome TEXT NOT NULL CHECK (Outcome IN ('COMPLETED', 'STOPPED', 'FAILED')),
                    CompletedSearches INTEGER NOT NULL,
                    TotalSearches INTEGER NOT NULL,
                    Found INTEGER NOT NULL,
                    NotFound INTEGER NOT NULL,
                    Errors INTEGER NOT NULL,
                    PrivilegedParticipants INTEGER NOT NULL,
                    MissingActivityProfiles INTEGER NOT NULL,
                    Details TEXT NOT NULL,
                    NotFoundFollowers INTEGER,
                    NotFoundFollowing INTEGER,
                    Directions INTEGER NOT NULL DEFAULT 3
                );
                """;
            create.ExecuteNonQuery();
        }
        EnsureColumn(connection, transaction, "NotFoundFollowers");
        EnsureColumn(connection, transaction, "NotFoundFollowing");
        EnsureColumn(connection, transaction, "Directions", "INTEGER NOT NULL DEFAULT 3");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO InstagramBatchSummaries
                (TargetUserName, FinishedAtUtc, Outcome, CompletedSearches, TotalSearches,
                 Found, NotFound, Errors, PrivilegedParticipants, MissingActivityProfiles, Details,
                 NotFoundFollowers, NotFoundFollowing, Directions)
            VALUES ($target, $at, $outcome, $completed, $total,
                    $found, $notFound, $errors, $privileged, $missing, $details,
                    $notFoundFollowers, $notFoundFollowing, $directions)
            ON CONFLICT(TargetUserName) DO UPDATE SET
                FinishedAtUtc=excluded.FinishedAtUtc,
                Outcome=excluded.Outcome,
                CompletedSearches=excluded.CompletedSearches,
                TotalSearches=excluded.TotalSearches,
                Found=excluded.Found,
                NotFound=excluded.NotFound,
                Errors=excluded.Errors,
                PrivilegedParticipants=excluded.PrivilegedParticipants,
                MissingActivityProfiles=excluded.MissingActivityProfiles,
                Details=excluded.Details,
                NotFoundFollowers=excluded.NotFoundFollowers,
                NotFoundFollowing=excluded.NotFoundFollowing,
                Directions=excluded.Directions
            """;
        command.Parameters.AddWithValue("$target", NormalizeTarget(targetUserName));
        command.Parameters.AddWithValue("$at", summary.FinishedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$outcome", summary.Outcome.ToString());
        command.Parameters.AddWithValue("$completed", summary.CompletedSearches);
        command.Parameters.AddWithValue("$total", summary.TotalSearches);
        command.Parameters.AddWithValue("$found", summary.Found);
        command.Parameters.AddWithValue("$notFound", summary.NotFound);
        command.Parameters.AddWithValue("$errors", summary.Errors);
        command.Parameters.AddWithValue("$privileged", summary.PrivilegedParticipants);
        command.Parameters.AddWithValue("$missing", summary.MissingActivityProfiles);
        command.Parameters.AddWithValue("$details", summary.Details);
        command.Parameters.AddWithValue("$notFoundFollowers", (object?)summary.NotFoundFollowers ?? DBNull.Value);
        command.Parameters.AddWithValue("$notFoundFollowing", (object?)summary.NotFoundFollowing ?? DBNull.Value);
        command.Parameters.AddWithValue("$directions", (int)summary.Directions);
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction, string column,
        string definition = "INTEGER")
    {
        using var info = connection.CreateCommand();
        info.Transaction = transaction;
        info.CommandText = "PRAGMA table_info(InstagramBatchSummaries)";
        using var reader = info.ExecuteReader();
        var exists = false;
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                exists = true;
        reader.Close();
        if (exists) return;
        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE InstagramBatchSummaries ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    public InstagramBatchResultSummary? Read(string targetUserName)
    {
        if (string.IsNullOrWhiteSpace(targetUserName) || !File.Exists(databasePath)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='InstagramBatchSummaries'";
        if (exists.ExecuteScalar() is null) return null;
        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(InstagramBatchSummaries)";
        using var columnsReader = columns.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (columnsReader.Read()) names.Add(columnsReader.GetString(1));
        columnsReader.Close();
        var followers = names.Contains("NotFoundFollowers") ? "NotFoundFollowers" : "NULL";
        var following = names.Contains("NotFoundFollowing") ? "NotFoundFollowing" : "NULL";
        var directions = names.Contains("Directions") ? "Directions" : "3";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT FinishedAtUtc, Outcome, CompletedSearches, TotalSearches, Found, NotFound,
                   Errors, PrivilegedParticipants, MissingActivityProfiles, Details,
                   {followers}, {following}, {directions}
            FROM InstagramBatchSummaries WHERE TargetUserName=$target AND Outcome='COMPLETED'
            """;
        command.Parameters.AddWithValue("$target", NormalizeTarget(targetUserName));
        using var reader = command.ExecuteReader();
        if (!reader.Read() ||
            !DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var finishedAt)) return null;
        return new InstagramBatchResultSummary(
            finishedAt, InstagramBatchOutcome.COMPLETED, reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            (InstagramCheckDirections)reader.GetInt32(12));
    }

    internal static string NormalizeTarget(string userName) => userName.Trim().TrimStart('@');
}
