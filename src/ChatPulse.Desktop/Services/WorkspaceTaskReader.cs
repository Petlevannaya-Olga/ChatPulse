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
        var command = connection.CreateCommand();
        command.CommandText = hasReactionStates ? """
            SELECT message.MessageId, message.Text, reaction.HasOwnReaction
            FROM ImportedChatMessages AS message
            LEFT JOIN ImportedMessageReactionStates AS reaction
              ON reaction.TelegramUserId = message.TelegramUserId
             AND reaction.PeerType = message.PeerType
             AND reaction.PeerId = message.PeerId AND reaction.MessageId = message.MessageId
            WHERE message.TelegramUserId = $owner AND message.PeerType = $peerType AND message.PeerId = $peerId
            ORDER BY message.MessageId DESC
            """ : """
            SELECT MessageId, Text, NULL
            FROM ImportedChatMessages
            WHERE TelegramUserId = $owner AND PeerType = $peerType AND PeerId = $peerId
            ORDER BY MessageId DESC
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
            var evidence = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(messageId, messageText));
            if (evidence.Classification != ChatMessageClassification.TASK || evidence.PostLinks.Count == 0)
                continue;
            if (!string.Equals(evidence.Network?.ToString(), chat.SocialNetwork,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            tasks.Add(new WorkspaceDetectedTask(messageId, messageText, evidence.PostLinks[0],
                !reader.IsDBNull(2) && reader.GetInt64(2) == 1));
        }
        return tasks;
    }
}

public sealed record WorkspaceDetectedTask(long MessageId, string Text, string PostUrl, bool IsCompleted = false);
