using System.IO;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Locally removes a completed card from the workspace, without deleting Telegram
/// history or reactions. The tombstone survives subsequent imports and refreshes.
/// </summary>
public sealed class WorkspaceCompletedTaskHideStore(string databasePath)
{
    public async Task HideAsync(ConfiguredChat chat, long messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        if (!File.Exists(databasePath))
            throw new InvalidOperationException("История Telegram ещё не загружена.");

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ImportedMessageHidden (
                TelegramUserId INTEGER NOT NULL,
                PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO ImportedMessageHidden
                (TelegramUserId, PeerType, PeerId, MessageId)
            SELECT $owner, $peerType, $peerId, $messageId
            WHERE EXISTS (
                SELECT 1 FROM ImportedChatMessages AS message
                INNER JOIN ImportedMessageReactionStates AS reaction
                    ON reaction.TelegramUserId = message.TelegramUserId
                   AND reaction.PeerType = message.PeerType
                   AND reaction.PeerId = message.PeerId
                   AND reaction.MessageId = message.MessageId
                WHERE message.TelegramUserId = $owner
                  AND message.PeerType = $peerType
                  AND message.PeerId = $peerId
                  AND message.MessageId = $messageId
                  AND reaction.HasOwnReaction = 1
            );
            """;
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$peerType", chat.PeerType);
        command.Parameters.AddWithValue("$peerId", chat.PeerId);
        command.Parameters.AddWithValue("$messageId", messageId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            var check = connection.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM ImportedMessageHidden
                WHERE TelegramUserId = $owner AND PeerType = $peerType
                  AND PeerId = $peerId AND MessageId = $messageId
                """;
            check.Parameters.AddWithValue("$owner", chat.TelegramUserId);
            check.Parameters.AddWithValue("$peerType", chat.PeerType);
            check.Parameters.AddWithValue("$peerId", chat.PeerId);
            check.Parameters.AddWithValue("$messageId", messageId);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
                throw new InvalidOperationException("Можно удалить только выполненное задание выбранного чата.");
        }
    }
}
