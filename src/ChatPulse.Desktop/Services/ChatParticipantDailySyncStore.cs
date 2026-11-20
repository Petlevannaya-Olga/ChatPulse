using System.Globalization;
using System.IO;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Commits a verified batch of Telegram lists to BOTH registers and the live
/// source cursor in one SQLite transaction. No per-list commits are allowed.
/// </summary>
public sealed class ChatParticipantDailySyncStore(string databasePath)
{
    public async Task<IReadOnlySet<string>> ReadKnownUsernamesAsync(ConfiguredChat chat,
        CancellationToken cancellationToken = default)
    {
        var known = new HashSet<string>(
            await new ChatParticipantHistoryStore(databasePath)
                .ReadKnownUsernamesAsync(chat, cancellationToken), StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(databasePath)) return known;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='ChatParticipants'";
        if (await exists.ExecuteScalarAsync(cancellationToken) is null) return known;
        using var command = Command(connection, null, """
            SELECT SubscriptionProfileUrl FROM ChatParticipants
            WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
            """);
        AddChat(command, chat);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (TryUsername(reader.GetString(0), out var username)) known.Add(username);
        }
        return known;
    }

    public async Task CommitAsync(ConfiguredChat chat,
        IReadOnlyList<ParticipantHistoryCandidate> verifiedLists,
        ParticipantHistoryRule rule, long? expectedLiveMessageId,
        string outcomeDescription, CancellationToken cancellationToken = default,
        IReadOnlyList<ConfiguredChat>? configuredChats = null,
        string? instagramTarget = null, string? browserAccount = null)
    {
        if (verifiedLists.Count == 0) return;
        if (verifiedLists.Any(item => item.Entries is null ||
            !string.IsNullOrWhiteSpace(item.ParseErrorDescription)))
            throw new ArgumentException("Пакет содержит непроверенный список.", nameof(verifiedLists));
        var sorted = verifiedLists.OrderBy(item => item.MessageId).ToArray();
        var latest = sorted[^1];
        var unique = ChatParticipantHistoryStore.VerifiedUniqueEntries(latest);
        // Validate EVERY list (including duplicates) before beginning any write.
        foreach (var list in sorted) ChatParticipantHistoryStore.VerifiedUniqueEntries(list);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await EnsureLiveSchemaAsync(connection, cancellationToken);
        await ChatParticipantHistoryStore.EnsureHistorySchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var reviewChats = configuredChats ?? new[] { chat };
            var ownNames = InstagramSubscriptionReviewStateStore.ReadOwnNames(connection,
                transaction, instagramTarget, browserAccount);
            var reviewBefore = InstagramSubscriptionReviewStateStore.Capture(connection,
                transaction, reviewChats, ownNames);
            using (var source = Command(connection, transaction, """
                SELECT SourceMessageId FROM ChatParticipantRegistryVersions
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                """))
            {
                AddChat(source, chat);
                var actual = await source.ExecuteScalarAsync(cancellationToken);
                if ((actual is null ? (long?)null : Convert.ToInt64(actual, CultureInfo.InvariantCulture)) !=
                    expectedLiveMessageId)
                    throw new InvalidOperationException(
                        "Список участниц был изменён другим процессом. Повторите синхронизацию.");
            }

            await ChatParticipantHistoryStore.AppendVerifiedBatchAsync(connection, transaction,
                chat, sorted, rule, cancellationToken);

            var existing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var read = Command(connection, transaction, """
                SELECT ParticipantId, SubscriptionProfileUrl FROM ChatParticipants
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                ORDER BY ParticipantId
                """))
            {
                AddChat(read, chat);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!TryUsername(reader.GetString(1), out var username)) continue;
                    // Legacy versions could store the same username more than once.
                    // The product identity is ChatId + username: retain the earliest
                    // stable ParticipantId and make the redundant rows inactive.
                    existing.TryAdd(username, reader.GetInt64(0));
                }
            }

            using (var clear = Command(connection, transaction, """
                UPDATE ChatParticipants SET IsCurrent=0
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                """))
            {
                AddChat(clear, chat);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var participant in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryUsername(participant.SubscriptionProfileUrl, out var username))
                    throw new InvalidOperationException("Недопустимый Instagram username в проверенном списке.");
                var hasExisting = existing.TryGetValue(username, out var id);
                using var command = Command(connection, transaction, hasExisting ? """
                    UPDATE ChatParticipants SET Position=$position, DisplayName=$name,
                        SubscriptionProfileUrl=$subscription, ActivityProfileUrl=$activity,
                        Category=$category, Status=$status, IsCurrent=1, SourceMessageId=$source
                    WHERE ParticipantId=$id AND TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                    """ : """
                    INSERT INTO ChatParticipants
                        (TelegramUserId, PeerType, PeerId, Position, DisplayName,
                         SubscriptionProfileUrl, ActivityProfileUrl, Category,
                         Status, IsCurrent, SourceMessageId)
                    VALUES ($owner, $type, $peer, $position, $name,
                            $subscription, $activity, $category, $status, 1, $source)
                    """);
                AddChat(command, chat);
                if (hasExisting) command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$position", (object?)participant.Position ?? DBNull.Value);
                command.Parameters.AddWithValue("$name", (object?)participant.DisplayName ?? DBNull.Value);
                command.Parameters.AddWithValue("$subscription", participant.SubscriptionProfileUrl);
                command.Parameters.AddWithValue("$activity", participant.ActivityProfileUrl);
                command.Parameters.AddWithValue("$category", participant.Category);
                command.Parameters.AddWithValue("$status", participant.IsPenalty ? "PENALTY" :
                    participant.IsWeekend ? "WEEKEND" : "ACTIVE");
                command.Parameters.AddWithValue("$source", latest.MessageId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            using (var cursor = Command(connection, transaction, """
                INSERT INTO ChatParticipantRegistryVersions
                    (TelegramUserId, PeerType, PeerId, SourceMessageId,
                     SourceSentAtUnixSeconds, LastSuccessfulSyncAtUnixSeconds)
                VALUES ($owner, $type, $peer, $source, $sent, $sync)
                ON CONFLICT(TelegramUserId, PeerType, PeerId)
                DO UPDATE SET SourceMessageId=excluded.SourceMessageId,
                    SourceSentAtUnixSeconds=excluded.SourceSentAtUnixSeconds,
                    LastSuccessfulSyncAtUnixSeconds=excluded.LastSuccessfulSyncAtUnixSeconds
                """))
            {
                AddChat(cursor, chat);
                cursor.Parameters.AddWithValue("$source", latest.MessageId);
                cursor.Parameters.AddWithValue("$sent", latest.SentAtUtc is { } sent
                    ? (object)new DateTimeOffset(sent.ToUniversalTime()).ToUnixTimeSeconds()
                    : DBNull.Value);
                cursor.Parameters.AddWithValue("$sync", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await cursor.ExecuteNonQueryAsync(cancellationToken);
            }
            using (var attempt = Command(connection, transaction, """
                INSERT INTO ChatParticipantRegistryRefreshAttempts
                    (TelegramUserId, PeerType, PeerId, SourceMessageId,
                     SourceSentAtUnixSeconds, AttemptedAtUnixSeconds,
                     Status, Description)
                VALUES ($owner, $type, $peer, $message, $sent, $at, 'APPLIED', $description)
                ON CONFLICT(TelegramUserId, PeerType, PeerId) DO UPDATE SET
                    SourceMessageId=excluded.SourceMessageId,
                    SourceSentAtUnixSeconds=excluded.SourceSentAtUnixSeconds,
                    AttemptedAtUnixSeconds=excluded.AttemptedAtUnixSeconds,
                    Status=excluded.Status, Description=excluded.Description
                """))
            {
                AddChat(attempt, chat);
                attempt.Parameters.AddWithValue("$message", latest.MessageId);
                attempt.Parameters.AddWithValue("$sent", latest.SentAtUtc is { } sent
                    ? (object)new DateTimeOffset(sent.ToUniversalTime()).ToUnixTimeSeconds()
                    : DBNull.Value);
                attempt.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                attempt.Parameters.AddWithValue("$description", outcomeDescription);
                await attempt.ExecuteNonQueryAsync(cancellationToken);
            }
            var reviewAfter = InstagramSubscriptionReviewStateStore.Capture(connection,
                transaction, reviewChats, ownNames);
            InstagramSubscriptionReviewStateStore.RecordChanges(connection, transaction,
                instagramTarget, reviewBefore, reviewAfter);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static bool TryUsername(string profile, out string username)
    {
        username = string.Empty;
        if (!Uri.TryCreate(profile, UriKind.Absolute, out var uri) ||
            uri.Host is not ("instagram.com" or "www.instagram.com")) return false;
        var path = uri.AbsolutePath.Trim('/');
        if (path.Contains('/') || path.Length == 0) return false;
        try { username = InstagramUserName.Normalize(Uri.UnescapeDataString(path)); }
        catch (ArgumentException) { return false; }
        return true;
    }

    private static async Task EnsureLiveSchemaAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = Command(connection, null, """
            CREATE TABLE IF NOT EXISTS ChatParticipantRegistryVersions (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                SourceMessageId INTEGER NOT NULL, SourceSentAtUnixSeconds INTEGER,
                LastSuccessfulSyncAtUnixSeconds INTEGER,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId)
            );
            CREATE TABLE IF NOT EXISTS ChatParticipants (
                ParticipantId INTEGER PRIMARY KEY AUTOINCREMENT,
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                Position INTEGER, DisplayName TEXT, SubscriptionProfileUrl TEXT NOT NULL,
                ActivityProfileUrl TEXT NOT NULL, Category TEXT NOT NULL,
                Status TEXT NOT NULL, IsCurrent INTEGER NOT NULL CHECK (IsCurrent IN (0, 1)),
                SourceMessageId INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ChatParticipants_Chat
                ON ChatParticipants(TelegramUserId, PeerType, PeerId);
            CREATE TABLE IF NOT EXISTS ChatParticipantRegistryRefreshAttempts (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                SourceMessageId INTEGER, SourceSentAtUnixSeconds INTEGER,
                AttemptedAtUnixSeconds INTEGER NOT NULL, Status TEXT NOT NULL,
                Description TEXT NOT NULL,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId)
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
        // Legacy installations may predate the source-date and sync-date columns.
        using var pragma = Command(connection, null, "PRAGMA table_info(ChatParticipantRegistryVersions)");
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await pragma.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        foreach (var column in new[] { "SourceSentAtUnixSeconds", "LastSuccessfulSyncAtUnixSeconds" })
        {
            if (columns.Contains(column)) continue;
            using var migration = Command(connection, null,
                $"ALTER TABLE ChatParticipantRegistryVersions ADD COLUMN {column} INTEGER");
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddChat(SqliteCommand command, ConfiguredChat chat)
    {
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$type", chat.PeerType);
        command.Parameters.AddWithValue("$peer", chat.PeerId);
    }
}
