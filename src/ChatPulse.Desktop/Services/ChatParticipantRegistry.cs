using System.IO;
using System.Globalization;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

public sealed record ParticipantListSourceInfo(long MessageId, DateTime? SentAtUtc,
    DateTime? LastSuccessfulSyncAtUtc = null)
{
    public string DateLabel => SentAtUtc is { } date
        ? date.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
        : "Дата списка неизвестна";

    public string LastSuccessfulSyncLabel => LastSuccessfulSyncAtUtc is { } date
        ? date.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
        : "Дата синхронизации неизвестна";
}

public enum ParticipantListApplyStatus
{
    NO_VALID_LIST,
    APPLIED,
    ALREADY_CURRENT,
    OLDER_MESSAGE,
    AMBIGUOUS_MATCH,
    PROFILE_CHECK_INCONCLUSIVE,
    UNPARSEABLE_MESSAGE
}

public sealed record ParticipantListApplyResult(int AppliedCount, ParticipantListApplyStatus Status);

public sealed record ParticipantListRefreshAttempt(long? MessageId, DateTime? SentAtUtc,
    DateTime AttemptedAtUtc, ParticipantListApplyStatus Status, string Description);

/// <summary>Stores each recognized full participant register as a chat-local snapshot.</summary>
public sealed class ChatParticipantRegistry(string databasePath)
{
    public Task<int> UpdateAsync(ConfiguredChat chat,
        IEnumerable<(long MessageId, string? Text)> messages,
        CancellationToken cancellationToken = default)
        => UpdateAsync(chat, messages.Select(item =>
            (item.MessageId, item.Text, SentAtUtc: (DateTime?)null)), cancellationToken);

    public async Task<int> UpdateAsync(ConfiguredChat chat,
        IEnumerable<(long MessageId, string? Text, DateTime? SentAtUtc)> messages,
        CancellationToken cancellationToken = default)
        => (await UpdateDetailedAsync(chat, messages, cancellationToken)).AppliedCount;

    /// <summary>
    /// Applies complete lists in ascending message order and explains why the last
    /// recognized message was (or was not) accepted. Older/ambiguous snapshots never
    /// replace the last successfully applied list.
    /// </summary>
    public async Task<ParticipantListApplyResult> UpdateDetailedAsync(ConfiguredChat chat,
        IEnumerable<(long MessageId, string? Text, DateTime? SentAtUtc)> messages,
        CancellationToken cancellationToken = default, Func<string, bool?>? profileOpens = null)
    {
        var profileCheckFailed = false;
        var snapshots = messages.OrderBy(item => item.MessageId).Select(item =>
        {
            var instagramOnly = chat.SocialNetwork.Equals("Instagram", StringComparison.OrdinalIgnoreCase);
            var allowDamagedPenaltyDomains = ChatParticipantHistoryRuleSelection.SupportsChat14(chat);
            var valid = ParticipantListParser.TryParse(item.Text, out var parsed, profileOpens,
                instagramOnly, allowDamagedPenaltyDomains);
            if (valid && parsed is not null && parsed.Participants.Any(participant =>
                    participant.ProfileRequiresVerification))
            {
                profileCheckFailed = true;
                valid = false;
            }
            else if (!valid && profileOpens is not null && ParticipantListParser.TryParse(item.Text, out _,
                instagramOnly: instagramOnly,
                allowDamagedPenaltyDomains: allowDamagedPenaltyDomains))
                profileCheckFailed = true;
            return (item.MessageId, item.SentAtUtc, Valid: valid, Parsed: parsed);
        }).Where(item => item.Valid && item.Parsed is not null).ToArray();
        if (snapshots.Length == 0)
            return new ParticipantListApplyResult(0, profileCheckFailed
                ? ParticipantListApplyStatus.PROFILE_CHECK_INCONCLUSIVE
                : ParticipantListApplyStatus.NO_VALID_LIST);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ChatParticipantRegistryVersions (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                SourceMessageId INTEGER NOT NULL,
                SourceSentAtUnixSeconds INTEGER,
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
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken);
        // Existing installations have no timestamp column: migrate in place.
        var schema = connection.CreateCommand();
        schema.CommandText = "PRAGMA table_info(ChatParticipantRegistryVersions)";
        var hasTimestamp = false;
        var hasLastSuccessfulSync = false;
        await using (var columns = await schema.ExecuteReaderAsync(cancellationToken))
        {
            while (await columns.ReadAsync(cancellationToken))
            {
                if (string.Equals(columns.GetString(1), "SourceSentAtUnixSeconds", StringComparison.Ordinal))
                    hasTimestamp = true;
                if (string.Equals(columns.GetString(1), "LastSuccessfulSyncAtUnixSeconds", StringComparison.Ordinal))
                    hasLastSuccessfulSync = true;
            }
        }
        if (!hasTimestamp)
        {
            var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE ChatParticipantRegistryVersions ADD COLUMN SourceSentAtUnixSeconds INTEGER";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!hasLastSuccessfulSync)
        {
            var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE ChatParticipantRegistryVersions ADD COLUMN LastSuccessfulSyncAtUnixSeconds INTEGER";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        var applied = 0;
        var status = ParticipantListApplyStatus.NO_VALID_LIST;
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = """
                SELECT SourceMessageId FROM ChatParticipantRegistryVersions
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                """;
            AddChatParameters(version, chat);
            var prior = await version.ExecuteScalarAsync(cancellationToken);
            if (prior is not null && Convert.ToInt64(prior) > snapshot.MessageId)
            {
                await transaction.CommitAsync(cancellationToken);
                status = ParticipantListApplyStatus.OLDER_MESSAGE;
                continue;
            }
            var current = connection.CreateCommand();
            current.Transaction = transaction;
            current.CommandText = """
                SELECT ParticipantId, SubscriptionProfileUrl, ActivityProfileUrl, DisplayName,
                       IsCurrent, Position, Category, Status
                FROM ChatParticipants
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                """;
            AddChatParameters(current, chat);
            var known = new List<(long Id, string Subscription, string Activity, string? Name,
                bool IsCurrent, int? Position, string Category, string Status)>();
            await using (var reader = await current.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    known.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4) == 1,
                        reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        reader.GetString(6), reader.GetString(7)));
            }
            // The same message can be returned again during a manual refresh.
            // Avoid rewriting the register if its complete contents have not changed.
            // An edited Telegram message with the same ID is still applied below.
            if (prior is not null && Convert.ToInt64(prior) == snapshot.MessageId &&
                SnapshotMatchesCurrent(snapshot.Parsed!.Participants, known))
            {
                var markSynced = connection.CreateCommand();
                markSynced.Transaction = transaction;
                markSynced.CommandText = """
                    UPDATE ChatParticipantRegistryVersions
                    SET LastSuccessfulSyncAtUnixSeconds=$synced,
                        SourceSentAtUnixSeconds=COALESCE($sentAt, SourceSentAtUnixSeconds)
                    WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                    """;
                AddChatParameters(markSynced, chat);
                markSynced.Parameters.AddWithValue("$synced", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                markSynced.Parameters.AddWithValue("$sentAt", snapshot.SentAtUtc is { } existingSentAt
                    ? (object)new DateTimeOffset(existingSentAt.ToUniversalTime()).ToUnixTimeSeconds()
                    : DBNull.Value);
                await markSynced.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                status = ParticipantListApplyStatus.ALREADY_CURRENT;
                continue;
            }
            var matches = new List<(ParsedParticipant Participant, long? Id)>();
            var used = new HashSet<long>();
            var ambiguous = false;
            foreach (var participant in snapshot.Parsed!.Participants)
            {
                var bySubscription = known.Where(item => item.Subscription.Equals(
                    participant.SubscriptionProfileUrl, StringComparison.OrdinalIgnoreCase)).ToArray();
                var byActivity = known.Where(item => item.Activity.Equals(
                    participant.ActivityProfileUrl, StringComparison.OrdinalIgnoreCase)).ToArray();
                var possible = bySubscription.Concat(byActivity).DistinctBy(item => item.Id).ToArray();
                // Shared links are not proof of identical people. Only a unique
                // display-name match may disambiguate people with the same URL.
                if (possible.Length > 1 && !string.IsNullOrWhiteSpace(participant.DisplayName))
                    possible = possible.Where(item => string.Equals(item.Name,
                        participant.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
                var candidates = possible.Select(item => item.Id).ToArray();
                if (candidates.Length > 1 || candidates.Length == 1 && !used.Add(candidates[0]))
                {
                    ambiguous = true;
                    break;
                }
                matches.Add((participant, candidates.Length == 1 ? candidates[0] : null));
            }
            // Never corrupt the previous register by guessing who changed accounts.
            if (ambiguous)
            {
                await transaction.RollbackAsync(cancellationToken);
                status = ParticipantListApplyStatus.AMBIGUOUS_MATCH;
                continue;
            }
            var deactivate = connection.CreateCommand();
            deactivate.Transaction = transaction;
            deactivate.CommandText = """
                UPDATE ChatParticipants SET IsCurrent=0
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                """;
            AddChatParameters(deactivate, chat);
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
            foreach (var (participant, id) in matches)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = id.HasValue ? """
                    UPDATE ChatParticipants SET Position=$position, DisplayName=$name,
                        SubscriptionProfileUrl=$subscription, ActivityProfileUrl=$activity,
                        Category=$category, Status=$status, IsCurrent=1, SourceMessageId=$source
                    WHERE ParticipantId=$id AND TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                    """ : """
                    INSERT INTO ChatParticipants (TelegramUserId, PeerType, PeerId, Position, DisplayName,
                        SubscriptionProfileUrl, ActivityProfileUrl, Category, Status, IsCurrent, SourceMessageId)
                    VALUES ($owner, $type, $peer, $position, $name, $subscription, $activity,
                        $category, $status, 1, $source)
                    """;
                AddChatParameters(command, chat);
                command.Parameters.AddWithValue("$position", participant.Position is int number ? (object)number : DBNull.Value);
                command.Parameters.AddWithValue("$name", (object?)participant.DisplayName ?? DBNull.Value);
                command.Parameters.AddWithValue("$subscription", participant.SubscriptionProfileUrl);
                command.Parameters.AddWithValue("$activity", participant.ActivityProfileUrl);
                command.Parameters.AddWithValue("$category", participant.Category);
                command.Parameters.AddWithValue("$status", participant.Status.ToString());
                command.Parameters.AddWithValue("$source", snapshot.MessageId);
                if (id.HasValue) command.Parameters.AddWithValue("$id", id.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            var saveVersion = connection.CreateCommand();
            saveVersion.Transaction = transaction;
            saveVersion.CommandText = """
                INSERT INTO ChatParticipantRegistryVersions
                    (TelegramUserId, PeerType, PeerId, SourceMessageId, SourceSentAtUnixSeconds,
                     LastSuccessfulSyncAtUnixSeconds)
                VALUES ($owner, $type, $peer, $source, $sentAt, $synced)
                ON CONFLICT(TelegramUserId, PeerType, PeerId)
                DO UPDATE SET SourceMessageId=excluded.SourceMessageId,
                    LastSuccessfulSyncAtUnixSeconds=excluded.LastSuccessfulSyncAtUnixSeconds,
                    SourceSentAtUnixSeconds=CASE
                        WHEN excluded.SourceSentAtUnixSeconds IS NOT NULL THEN excluded.SourceSentAtUnixSeconds
                        WHEN ChatParticipantRegistryVersions.SourceMessageId=excluded.SourceMessageId
                            THEN ChatParticipantRegistryVersions.SourceSentAtUnixSeconds
                        ELSE NULL
                    END
                """;
            AddChatParameters(saveVersion, chat);
            saveVersion.Parameters.AddWithValue("$source", snapshot.MessageId);
            saveVersion.Parameters.AddWithValue("$synced", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            saveVersion.Parameters.AddWithValue("$sentAt", snapshot.SentAtUtc is { } sentAt
                ? (object)new DateTimeOffset(sentAt.ToUniversalTime()).ToUnixTimeSeconds()
                : DBNull.Value);
            await saveVersion.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            applied++;
            status = ParticipantListApplyStatus.APPLIED;
        }
        return new ParticipantListApplyResult(applied, status);
    }

    private static bool SnapshotMatchesCurrent(IEnumerable<ParsedParticipant> participants,
        IReadOnlyList<(long Id, string Subscription, string Activity, string? Name,
            bool IsCurrent, int? Position, string Category, string Status)> stored)
    {
        var current = participants.ToArray();
        if (stored.Count(item => item.IsCurrent) != current.Length) return false;
        var used = new HashSet<long>();
        foreach (var participant in current)
        {
            var matches = stored.Where(item => item.IsCurrent &&
                item.Position == participant.Position &&
                string.Equals(item.Name, participant.DisplayName, StringComparison.Ordinal) &&
                string.Equals(item.Subscription, participant.SubscriptionProfileUrl,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Activity, participant.ActivityProfileUrl,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Category, participant.Category, StringComparison.Ordinal) &&
                string.Equals(item.Status, participant.Status.ToString(), StringComparison.Ordinal))
                .Select(item => item.Id).ToArray();
            if (matches.Length != 1 || !used.Add(matches[0])) return false;
        }
        return true;
    }

    /// <summary>Timestamp of the Telegram message whose list is currently applied to the chat.</summary>
    public async Task<ParticipantListSourceInfo?> ReadLatestSourceAsync(ConfiguredChat chat,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return null;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var table = connection.CreateCommand();
        table.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='ChatParticipantRegistryVersions'";
        if (await table.ExecuteScalarAsync(cancellationToken) is null) return null;
        var schema = connection.CreateCommand();
        schema.CommandText = "PRAGMA table_info(ChatParticipantRegistryVersions)";
        var hasTimestamp = false;
        var hasLastSuccessfulSync = false;
        await using (var columns = await schema.ExecuteReaderAsync(cancellationToken))
        {
            while (await columns.ReadAsync(cancellationToken))
            {
                if (string.Equals(columns.GetString(1), "SourceSentAtUnixSeconds", StringComparison.Ordinal))
                    hasTimestamp = true;
                if (string.Equals(columns.GetString(1), "LastSuccessfulSyncAtUnixSeconds", StringComparison.Ordinal))
                    hasLastSuccessfulSync = true;
            }
        }
        var query = connection.CreateCommand();
        query.CommandText = $"""
            SELECT SourceMessageId, {(hasTimestamp ? "SourceSentAtUnixSeconds" : "NULL")},
                   {(hasLastSuccessfulSync ? "LastSuccessfulSyncAtUnixSeconds" : "NULL")}
            FROM ChatParticipantRegistryVersions
            WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
            """;
        AddChatParameters(query, chat);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var time = reader.IsDBNull(1) ? (DateTime?)null
            : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).UtcDateTime;
        var synced = reader.IsDBNull(2) ? (DateTime?)null
            : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).UtcDateTime;
        return new ParticipantListSourceInfo(reader.GetInt64(0), time, synced);
    }

    /// <summary>
    /// Persists the latest refresh outcome separately from the last successfully
    /// applied list. Failed parsing must remain visible in the report after restart.
    /// </summary>
    public async Task SaveLastAttemptAsync(ConfiguredChat chat, long? messageId,
        DateTime? sentAtUtc, ParticipantListApplyStatus status, string description,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ChatParticipantRegistryRefreshAttempts (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                SourceMessageId INTEGER, SourceSentAtUnixSeconds INTEGER,
                AttemptedAtUnixSeconds INTEGER NOT NULL,
                Status TEXT NOT NULL, Description TEXT NOT NULL,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId)
            );
            INSERT INTO ChatParticipantRegistryRefreshAttempts (
                TelegramUserId, PeerType, PeerId, SourceMessageId,
                SourceSentAtUnixSeconds, AttemptedAtUnixSeconds, Status, Description)
            VALUES ($owner, $type, $peer, $message, $sent, $attempt, $status, $description)
            ON CONFLICT(TelegramUserId, PeerType, PeerId) DO UPDATE SET
                SourceMessageId=excluded.SourceMessageId,
                SourceSentAtUnixSeconds=excluded.SourceSentAtUnixSeconds,
                AttemptedAtUnixSeconds=excluded.AttemptedAtUnixSeconds,
                Status=excluded.Status, Description=excluded.Description;
            """;
        AddChatParameters(command, chat);
        command.Parameters.AddWithValue("$message", messageId is { } id ? (object)id : DBNull.Value);
        command.Parameters.AddWithValue("$sent", sentAtUtc is { } date
            ? (object)new DateTimeOffset(date.ToUniversalTime()).ToUnixTimeSeconds() : DBNull.Value);
        command.Parameters.AddWithValue("$attempt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$description", description);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ParticipantListRefreshAttempt?> ReadLastAttemptAsync(ConfiguredChat chat,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return null;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var table = connection.CreateCommand();
        table.CommandText = """
            SELECT 1 FROM sqlite_master
            WHERE type='table' AND name='ChatParticipantRegistryRefreshAttempts'
            """;
        if (await table.ExecuteScalarAsync(cancellationToken) is null) return null;
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SourceMessageId, SourceSentAtUnixSeconds, AttemptedAtUnixSeconds,
                   Status, Description
            FROM ChatParticipantRegistryRefreshAttempts
            WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
            """;
        AddChatParameters(command, chat);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!Enum.TryParse<ParticipantListApplyStatus>(reader.GetString(3), out var status))
            return null;
        return new ParticipantListRefreshAttempt(
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.IsDBNull(1) ? null
                : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).UtcDateTime,
            status, reader.GetString(4));
    }

    private static void AddChatParameters(SqliteCommand command, ConfiguredChat chat)
    {
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$type", chat.PeerType);
        command.Parameters.AddWithValue("$peer", chat.PeerId);
    }
}
