using System.IO;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>Local evidence recorded only after Telegram confirms a reaction request.</summary>
public sealed class WorkspaceHeartReactionStore(string databasePath)
{
    public async Task<bool> RequiresReactionAsync(ConfiguredChat chat, long messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (messageId <= 0 || messageId > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(messageId));
        if (!File.Exists(databasePath))
            throw new InvalidOperationException("История Telegram ещё не загружена.");

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM ImportedChatMessages
                WHERE TelegramUserId = $owner AND PeerType = $peerType
                  AND PeerId = $peerId AND MessageId = $id)
            """;
        SetParameters(command, chat, messageId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new InvalidOperationException("Сообщение не найдено в выбранном чате.");

        var skip = connection.CreateCommand();
        skip.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = 'ImportedMessageSkips'
            """;
        var hasSkips = Convert.ToInt64(await skip.ExecuteScalarAsync(cancellationToken)) != 0;
        if (hasSkips)
        {
            skip.CommandText = """
                SELECT EXISTS(SELECT 1 FROM ImportedMessageSkips
                WHERE TelegramUserId = $owner AND PeerType = $peerType
                  AND PeerId = $peerId AND MessageId = $id)
                """;
            SetParameters(skip, chat, messageId);
            if (Convert.ToInt64(await skip.ExecuteScalarAsync(cancellationToken)) == 1)
                throw new InvalidOperationException("Пропущенное сообщение сначала нужно вернуть в задания.");
        }

        var table = connection.CreateCommand();
        table.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = 'ImportedMessageReactionStates'
            """;
        if (Convert.ToInt64(await table.ExecuteScalarAsync(cancellationToken)) == 0) return true;
        var reaction = connection.CreateCommand();
        reaction.CommandText = """
            SELECT HasOwnReaction FROM ImportedMessageReactionStates
            WHERE TelegramUserId = $owner AND PeerType = $peerType
              AND PeerId = $peerId AND MessageId = $id
            """;
        SetParameters(reaction, chat, messageId);
        return Convert.ToInt64(await reaction.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1;
    }

    public async Task ConfirmAsync(ConfiguredChat chat, long messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ImportedMessageReactionStates (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL,
                HasOwnReaction INTEGER NOT NULL CHECK (HasOwnReaction IN (0, 1)),
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken);
        var save = connection.CreateCommand();
        save.CommandText = """
            INSERT INTO ImportedMessageReactionStates
                (TelegramUserId, PeerType, PeerId, MessageId, HasOwnReaction)
            SELECT $owner, $peerType, $peerId, $id, 1
            WHERE EXISTS (SELECT 1 FROM ImportedChatMessages
                WHERE TelegramUserId = $owner AND PeerType = $peerType
                  AND PeerId = $peerId AND MessageId = $id)
            ON CONFLICT(TelegramUserId, PeerType, PeerId, MessageId)
            DO UPDATE SET HasOwnReaction = 1
            """;
        SetParameters(save, chat, messageId);
        if (await save.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("Не удалось сохранить реакцию для сообщения.");
    }

    private static void SetParameters(SqliteCommand command, ConfiguredChat chat, long messageId)
    {
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$peerType", chat.PeerType);
        command.Parameters.AddWithValue("$peerId", chat.PeerId);
        command.Parameters.AddWithValue("$id", messageId);
    }
}
