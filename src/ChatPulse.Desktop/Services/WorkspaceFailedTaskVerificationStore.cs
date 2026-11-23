using System.IO;
using System.Text.Json;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

public sealed record WorkspaceFailedCheck(string Name, bool Confirmed, string Detail);

/// <summary>One unsuccessful verification attempt, kept separately from a successful hide.</summary>
public sealed record WorkspaceFailedTaskReport(
    DateTimeOffset CheckedAtUtc,
    string Kind,
    string ViewerUserName,
    string Target,
    IReadOnlyList<WorkspaceFailedCheck> Checks)
{
    public bool HasFailure => Checks.Count > 0 && Checks.Any(check => !check.Confirmed);

    public static WorkspaceFailedTaskReport FromPost(InstagramTaskVerificationResult result) =>
        new(DateTimeOffset.UtcNow, "Публикация", result.UserName, result.PostUrl,
            new (string Name, InstagramTaskCheckResult? Check)[]
            {
                ("Лайк", result.Like),
                ("Комментарий", result.Comment),
                ("Сохранение", result.Save),
                ("Репост", result.Repost)
            }.Where(item => item.Check is not null)
              .Select(item => new WorkspaceFailedCheck(item.Name,
                  item.Check!.Status == InstagramTaskCheckStatus.CONFIRMED,
                  item.Check.Detail)).ToArray());

    public static WorkspaceFailedTaskReport FromFollow(InstagramFollowTaskVerification result) =>
        new(DateTimeOffset.UtcNow,
            result.TaskKind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка",
            result.ViewerUserName,
            string.Join(", ", result.Results.Select(item => $"@{item.TargetUserName}")),
            result.Results.Select(item => new WorkspaceFailedCheck(
                $"{(result.TaskKind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка")} @{item.TargetUserName}",
                item.Check.Status == InstagramTaskCheckStatus.CONFIRMED,
                item.Check.Detail)).ToArray());

    public static WorkspaceFailedTaskReport FromError(string kind, string viewer,
        string target, IEnumerable<string> requestedChecks, string error) =>
        new(DateTimeOffset.UtcNow, kind, viewer, target,
            requestedChecks.Select(name => new WorkspaceFailedCheck(name, false, error)).ToArray());
}

/// <summary>
/// Stores append-only unsuccessful reports for one Telegram message. Unlike successful
/// verification, writing a report must never add an entry to ImportedMessageHidden.
/// </summary>
public sealed class WorkspaceFailedTaskVerificationStore(string databasePath)
{
    private const string SCHEMA = """
        CREATE TABLE IF NOT EXISTS ImportedMessageVerificationFailures (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TelegramUserId INTEGER NOT NULL,
            PeerType TEXT NOT NULL,
            PeerId INTEGER NOT NULL,
            MessageId INTEGER NOT NULL,
            CheckedAtUtc TEXT NOT NULL,
            ReportJson TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_ImportedMessageVerificationFailures_Message
        ON ImportedMessageVerificationFailures (
            TelegramUserId, PeerType, PeerId, MessageId, Id DESC);
        """;

    public async Task SaveAsync(ConfiguredChat chat, long messageId, WorkspaceFailedTaskReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(report);
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        if (!report.HasFailure) throw new InvalidOperationException("Отчёт не содержит неуспешных проверок.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ImportedMessageVerificationFailures
                (TelegramUserId, PeerType, PeerId, MessageId, CheckedAtUtc, ReportJson)
            SELECT $owner, $type, $peer, $message, $checked, $json
            WHERE EXISTS (
                SELECT 1 FROM ImportedChatMessages m
                INNER JOIN ImportedMessageReactionStates r
                    ON r.TelegramUserId=m.TelegramUserId AND r.PeerType=m.PeerType
                    AND r.PeerId=m.PeerId AND r.MessageId=m.MessageId
                WHERE m.TelegramUserId=$owner AND m.PeerType=$type
                  AND m.PeerId=$peer AND m.MessageId=$message AND r.HasOwnReaction=1
            );
            """;
        BindMessage(command, chat, messageId);
        command.Parameters.AddWithValue("$checked", report.CheckedAtUtc.ToString("O"));
        var safeReport = report with
        {
            Checks = report.Checks.Select(item => item with
            {
                Detail = item.Detail.Length > 2000 ? item.Detail[..2000] : item.Detail
            }).ToArray()
        };
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(safeReport));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Отчёт можно сохранить только для выполненного задания этого чата.");
    }

    public async Task<IReadOnlySet<long>> FindWithReportsAsync(ConfiguredChat chat,
        IReadOnlyCollection<long> messageIds, CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0) return new HashSet<long>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        var parameters = new List<string>();
        var index = 0;
        foreach (var id in messageIds.Distinct())
        {
            var parameter = "$m" + index++;
            parameters.Add(parameter);
            command.Parameters.AddWithValue(parameter, id);
        }
        command.CommandText = $"""
            SELECT DISTINCT MessageId FROM ImportedMessageVerificationFailures
            WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
              AND MessageId IN ({string.Join(",", parameters)})
            """;
        BindChat(command, chat);
        var found = new HashSet<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            found.Add(reader.GetInt64(0));
        return found;
    }

    public async Task<IReadOnlyList<WorkspaceFailedTaskReport>> ReadAsync(ConfiguredChat chat,
        long messageId, int limit = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ReportJson FROM ImportedMessageVerificationFailures
            WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
              AND MessageId=$message
            ORDER BY Id DESC LIMIT $limit
            """;
        BindMessage(command, chat, messageId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        var reports = new List<WorkspaceFailedTaskReport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var report = JsonSerializer.Deserialize<WorkspaceFailedTaskReport>(reader.GetString(0));
            if (report is not null) reports.Add(report);
        }
        return reports;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
            throw new InvalidOperationException("История Telegram ещё не загружена.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var setup = connection.CreateCommand();
            setup.CommandText = SCHEMA;
            await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void BindChat(SqliteCommand command, ConfiguredChat chat)
    {
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$type", chat.PeerType);
        command.Parameters.AddWithValue("$peer", chat.PeerId);
    }

    private static void BindMessage(SqliteCommand command, ConfiguredChat chat, long messageId)
    {
        BindChat(command, chat);
        command.Parameters.AddWithValue("$message", messageId);
    }
}
