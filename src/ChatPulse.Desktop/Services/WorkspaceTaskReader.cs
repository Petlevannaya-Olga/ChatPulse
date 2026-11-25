using System.IO;
using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Read-only preview of task-like messages from the imported Telegram history.
/// Status is derived only from saved, verified reactions of the authorized account.
/// </summary>
public sealed class WorkspaceTaskReader(string databasePath, bool pooling = true)
{
    public string DatabasePath => databasePath;
    public async Task<IReadOnlyList<WorkspaceDetectedTask>> GetTasksAsync(
        ConfiguredChat chat, CancellationToken cancellationToken = default)
    {
        var result = await ReadPageAsync(chat, WorkspaceTaskState.All, false, 0, int.MaxValue,
            cancellationToken).ConfigureAwait(false);
        // Preserve the original ordering contract for existing consumers/tests.
        return result.Tasks.OrderBy(task => task.MessageId).ToArray();
    }

    /// <summary>Loads one visible completed task by its stable Telegram message ID.</summary>
    public async Task<WorkspaceDetectedTask?> ReadCompletedByIdAsync(
        ConfiguredChat chat, long messageId, CancellationToken cancellationToken = default)
    {
        var page = await ReadPageAsync(chat, WorkspaceTaskState.Completed, false, 0, 1,
            cancellationToken, includeTotals: false, onlyMessageId: messageId).ConfigureAwait(false);
        return page.Tasks.FirstOrDefault();
    }

    // Classification must happen before offsetting: a message with a URL is not always a task.
    // The database cursor is streamed; only the requested page is retained in managed memory.
    public async Task<WorkspaceTaskPage> ReadPageAsync(ConfiguredChat chat, WorkspaceTaskState state,
        bool oldestFirst, int offset, int pageSize, CancellationToken cancellationToken = default,
        bool includeTotals = true, long? onlyMessageId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(pageSize);
        ArgumentNullException.ThrowIfNull(chat);
        if (!File.Exists(databasePath)) return new WorkspaceTaskPage([], 0, 0);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        };
        if (!pooling)
            connectionString.Pooling = false;
        await using var connection = new SqliteConnection(connectionString.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedChatMessages'";
        if ((long)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) == 0) return new WorkspaceTaskPage([], 0, 0);

        using var stateTable = connection.CreateCommand();
        stateTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageReactionStates'";
        var hasReactionStates = (long)(await stateTable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
        using var authorTable = connection.CreateCommand();
        authorTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageAuthors'";
        var hasAuthors = (long)(await authorTable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
        using var emojiTable = connection.CreateCommand();
        emojiTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageCustomEmoji'";
        var hasEmojis = (long)(await emojiTable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
        using var datesTable = connection.CreateCommand();
        datesTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageDates'";
        var hasDates = (long)(await datesTable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
        using var skipTable = connection.CreateCommand();
        skipTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageSkips'";
        var hasSkips = (long)(await skipTable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
        using var hiddenTable = connection.CreateCommand();
        hiddenTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageHidden'";
        var hasHidden = (long)(await hiddenTable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
        using var ownershipTable = connection.CreateCommand();
        ownershipTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageAuthorUsernames'";
        var hasOwnership = (long)(await ownershipTable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
        // Own posts are retained in ImportedChatMessages, but are never task cards,
        // pending counters or entries in manual/bulk verification queues.
        var ownPostPredicate = hasOwnership ? """
            AND NOT EXISTS (
                SELECT 1 FROM ImportedMessageAuthorUsernames AS identity
                WHERE identity.TelegramUserId = message.TelegramUserId
                  AND identity.PeerType = message.PeerType
                  AND identity.PeerId = message.PeerId
                  AND identity.MessageId = message.MessageId
                  AND identity.IsOwnAuthor = 1
            )
            """ : string.Empty;
        var hiddenPredicate = hasHidden ? """
            AND NOT EXISTS (
                SELECT 1 FROM ImportedMessageHidden AS hidden
                WHERE hidden.TelegramUserId = message.TelegramUserId
                  AND hidden.PeerType = message.PeerType
                  AND hidden.PeerId = message.PeerId AND hidden.MessageId = message.MessageId
            )
            """ : string.Empty;
        using var command = connection.CreateCommand();
        var ownReactionColumn = hasReactionStates ? "reaction.HasOwnReaction" : "NULL";
        var ownReactionJoin = hasReactionStates ? """
            LEFT JOIN ImportedMessageReactionStates AS reaction
              ON reaction.TelegramUserId = message.TelegramUserId
             AND reaction.PeerType = message.PeerType
             AND reaction.PeerId = message.PeerId AND reaction.MessageId = message.MessageId
            """ : string.Empty;
        var authorColumn = hasAuthors ? "author.AuthorName" : "NULL";
        var authorJoin = hasAuthors ? """
            LEFT JOIN ImportedMessageAuthors AS author
              ON author.TelegramUserId = message.TelegramUserId
             AND author.PeerType = message.PeerType
             AND author.PeerId = message.PeerId AND author.MessageId = message.MessageId
            """ : string.Empty;
        var emojiColumn = hasEmojis ? "emoji.EntitiesJson" : "NULL";
        var emojiJoin = hasEmojis ? """
            LEFT JOIN ImportedMessageCustomEmoji AS emoji
              ON emoji.TelegramUserId = message.TelegramUserId
             AND emoji.PeerType = message.PeerType
             AND emoji.PeerId = message.PeerId AND emoji.MessageId = message.MessageId
            """ : string.Empty;
        var timestampColumn = hasDates ? "dates.SentAtUnixSeconds" : "NULL";
        var timestampJoin = hasDates ? """
            LEFT JOIN ImportedMessageDates AS dates
              ON dates.TelegramUserId = message.TelegramUserId
             AND dates.PeerType = message.PeerType
             AND dates.PeerId = message.PeerId AND dates.MessageId = message.MessageId
            """ : string.Empty;
        var skipColumn = hasSkips ? "CASE WHEN skipped.MessageId IS NULL THEN 0 ELSE 1 END" : "0";
        var skipJoin = hasSkips ? """
            LEFT JOIN ImportedMessageSkips AS skipped
              ON skipped.TelegramUserId = message.TelegramUserId
             AND skipped.PeerType = message.PeerType
             AND skipped.PeerId = message.PeerId AND skipped.MessageId = message.MessageId
            """ : string.Empty;
        command.CommandText = $"""
            SELECT message.MessageId, message.Text, {ownReactionColumn}, {authorColumn}, {emojiColumn}, {timestampColumn}, {skipColumn}
            FROM ImportedChatMessages AS message
            {ownReactionJoin}
            {authorJoin}
            {emojiJoin}
            {timestampJoin}
            {skipJoin}
            WHERE message.TelegramUserId = $owner AND message.PeerType = $peerType AND message.PeerId = $peerId
            {hiddenPredicate}
            {ownPostPredicate}
            {(onlyMessageId.HasValue ? "AND message.MessageId = $onlyMessageId" : string.Empty)}
            ORDER BY CASE WHEN {timestampColumn} IS NULL THEN 1 ELSE 0 END ASC,
                     {timestampColumn} {(oldestFirst ? "ASC" : "DESC")},
                     message.MessageId {(oldestFirst ? "ASC" : "DESC")}
            """;
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$peerType", chat.PeerType);
        command.Parameters.AddWithValue("$peerId", chat.PeerId);
        if (onlyMessageId.HasValue)
            command.Parameters.AddWithValue("$onlyMessageId", onlyMessageId.Value);

        // Use the same candidate detection as participant synchronization. A damaged
        // register is not a task just because its full parsing fails.
        IChatParticipantHistoryParser listRecognizer = ChatParticipantHistoryRuleSelection.SupportsChat14(chat)
            ? new Chat14ParticipantHistoryParser()
            : new StandardParticipantHistoryParser(
                string.Equals(chat.SocialNetwork, "Instagram", StringComparison.OrdinalIgnoreCase));
        var tasks = new List<WorkspaceDetectedTask>(Math.Min(pageSize, 256));
        var matchingCount = 0;
        var nonSkippedCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var messageId = reader.GetInt64(0);
            var messageText = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            // Administrator join/leave announcements can contain many numbered URLs.
            // Such messages may look like participant registers by sheer link count,
            // but they remain task notifications rather than complete registers.
            var isFollowAnnouncement = WorkspaceFollowActions.TryParseAnnouncement(messageText, out _);
            if (!ParticipantListParser.HasAnyLink(messageText) ||
                (listRecognizer.IsCandidate(messageText) && !isFollowAnnouncement) ||
                ChatRulesMessageClassifier.IsChatRules(messageText)) continue;
            var evidence = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(messageId, messageText));
            var postUrl = evidence.PostLinks.FirstOrDefault() ?? string.Empty;
            var sourceUrl = string.Equals(chat.PeerType, "Channel", StringComparison.OrdinalIgnoreCase)
                ? $"https://t.me/c/{chat.PeerId}/{messageId}"
                : string.Empty;
            var authorName = reader.IsDBNull(3) ? "Автор неизвестен" : reader.GetString(3);
            DateTimeOffset? sentAt = reader.IsDBNull(5)
                ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5));
            var task = new WorkspaceDetectedTask(messageId, messageText, postUrl,
                !reader.IsDBNull(2) && reader.GetInt64(2) == 1, authorName, sourceUrl,
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4), sentAt, reader.GetInt64(6) == 1);
            if (!task.IsSkipped) nonSkippedCount++;
            var matches = state switch
            {
                WorkspaceTaskState.Pending => !task.IsSkipped && !task.IsCompleted,
                WorkspaceTaskState.Completed => !task.IsSkipped && task.IsCompleted,
                WorkspaceTaskState.Skipped => task.IsSkipped,
                _ => true
            };
            if (!matches) continue;
            if (matchingCount >= offset && tasks.Count < pageSize)
                tasks.Add(task);
            matchingCount++;
            if (!includeTotals && matchingCount >= (long)offset + pageSize) break;
        }
        return new WorkspaceTaskPage(tasks, matchingCount, nonSkippedCount);
    }
}

public enum WorkspaceTaskState { All, Pending, Completed, Skipped }

public sealed record WorkspaceTaskPage(IReadOnlyList<WorkspaceDetectedTask> Tasks, int Total, int NonSkippedTotal);

public sealed record WorkspaceDetectedTask(long MessageId, string Text, string PostUrl,
    bool IsCompleted = false, string AuthorName = "Автор неизвестен", string SourceUrl = "", string CustomEmojiJson = "", DateTimeOffset? SentAtUtc = null, bool IsSkipped = false);
