using System.IO;
using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Read-only preview of task-like messages from the imported Telegram history.
/// Status is derived only from saved, verified reactions of the authorized account.
/// </summary>
public sealed class WorkspaceTaskReader(string databasePath)
{
    public async Task<IReadOnlyList<WorkspaceDetectedTask>> GetTasksAsync(
        ConfiguredChat chat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (!File.Exists(databasePath)) return [];

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedChatMessages'";
        if ((long)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) == 0) return [];

        var stateTable = connection.CreateCommand();
        stateTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageReactionStates'";
        var hasReactionStates = (long)(await stateTable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
        var authorTable = connection.CreateCommand();
        authorTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageAuthors'";
        var hasAuthors = (long)(await authorTable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
        var command = connection.CreateCommand();
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
        command.CommandText = $"""
            SELECT message.MessageId, message.Text, {ownReactionColumn}, {authorColumn}
            FROM ImportedChatMessages AS message
            {ownReactionJoin}
            {authorJoin}
            WHERE message.TelegramUserId = $owner AND message.PeerType = $peerType AND message.PeerId = $peerId
            ORDER BY message.MessageId DESC
            """;
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$peerType", chat.PeerType);
        command.Parameters.AddWithValue("$peerId", chat.PeerId);

        var tasks = new List<WorkspaceDetectedTask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var messageId = reader.GetInt64(0);
            var messageText = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            // Messages without URLs and participant registers are not task cards.
            if (!ParticipantListParser.HasAnyLink(messageText) ||
                ParticipantListParser.TryParse(messageText, out _)) continue;
            var evidence = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(messageId, messageText));
            var postUrl = evidence.PostLinks.FirstOrDefault() ?? string.Empty;
            var sourceUrl = string.Equals(chat.PeerType, "Channel", StringComparison.OrdinalIgnoreCase)
                ? $"https://t.me/c/{chat.PeerId}/{messageId}"
                : string.Empty;
            var authorName = reader.IsDBNull(3) ? "Автор неизвестен" : reader.GetString(3);
            tasks.Add(new WorkspaceDetectedTask(messageId, messageText, postUrl,
                !reader.IsDBNull(2) && reader.GetInt64(2) == 1, authorName, sourceUrl));
        }
        return tasks;
    }
}

public sealed record WorkspaceDetectedTask(long MessageId, string Text, string PostUrl,
    bool IsCompleted = false, string AuthorName = "Автор неизвестен", string SourceUrl = "");
