using System.IO;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// A local user decision, independent of Telegram reactions. Skipping never sends
/// or removes a reaction, and Telegram synchronization must not overwrite this table.
/// </summary>
public sealed class WorkspaceTaskSkipStore(string databasePath)
{
    public async Task SetSkippedAsync(ConfiguredChat chat, long messageId, bool skipped,
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
            CREATE TABLE IF NOT EXISTS ImportedMessageSkips (
                TelegramUserId INTEGER NOT NULL,
                PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$peerType", chat.PeerType);
        command.Parameters.AddWithValue("$peerId", chat.PeerId);
        command.Parameters.AddWithValue("$messageId", messageId);
        command.CommandText = skipped ? """
            INSERT OR IGNORE INTO ImportedMessageSkips
                (TelegramUserId, PeerType, PeerId, MessageId)
            SELECT $owner, $peerType, $peerId, $messageId
            WHERE EXISTS (
                SELECT 1 FROM ImportedChatMessages
                WHERE TelegramUserId = $owner AND PeerType = $peerType
                  AND PeerId = $peerId AND MessageId = $messageId
            );
            """ : """
            DELETE FROM ImportedMessageSkips
            WHERE TelegramUserId = $owner AND PeerType = $peerType
              AND PeerId = $peerId AND MessageId = $messageId;
            """;
        var count = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (skipped && count == 0)
        {
            var check = connection.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM ImportedChatMessages
                WHERE TelegramUserId = $owner AND PeerType = $peerType
                  AND PeerId = $peerId AND MessageId = $messageId
                """;
            check.Parameters.AddWithValue("$owner", chat.TelegramUserId);
            check.Parameters.AddWithValue("$peerType", chat.PeerType);
            check.Parameters.AddWithValue("$peerId", chat.PeerId);
            check.Parameters.AddWithValue("$messageId", messageId);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
                throw new InvalidOperationException("Сообщение не найдено в истории выбранного чата.");
        }
    }
}
