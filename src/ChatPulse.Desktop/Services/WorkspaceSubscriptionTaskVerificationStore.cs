using System.IO;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Records confirmed follow/unfollow checks and hides the completed Telegram task atomically.
/// It never changes Telegram reactions or deletes the imported message.
/// </summary>
public sealed class WorkspaceSubscriptionTaskVerificationStore(string databasePath)
{
    public async Task PublishAsync(ConfiguredChat chat, long messageId,
        InstagramFollowTaskVerification verification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(verification);
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        if (!verification.CanHide) throw new InvalidOperationException("Подписка или отписка не подтверждена.");
        if (!File.Exists(databasePath)) throw new InvalidOperationException("История Telegram ещё не загружена.");

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using (var create = connection.CreateCommand())
        {
            create.Transaction = (SqliteTransaction)transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS ImportedMessageSubscriptionVerified (
                    TelegramUserId INTEGER NOT NULL,
                    PeerType TEXT NOT NULL,
                    PeerId INTEGER NOT NULL,
                    MessageId INTEGER NOT NULL,
                    InstagramUserName TEXT NOT NULL,
                    TargetUserName TEXT NOT NULL,
                    VerificationKind TEXT NOT NULL CHECK (VerificationKind IN ('SUBSCRIBE','UNSUBSCRIBE')),
                    VerifiedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
                );
                CREATE TABLE IF NOT EXISTS ImportedMessageHidden (
                    TelegramUserId INTEGER NOT NULL,
                    PeerType TEXT NOT NULL,
                    PeerId INTEGER NOT NULL,
                    MessageId INTEGER NOT NULL,
                    PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO ImportedMessageSubscriptionVerified (
                    TelegramUserId, PeerType, PeerId, MessageId,
                    InstagramUserName, TargetUserName, VerificationKind, VerifiedAtUtc)
                SELECT $owner, $type, $peer, $message, $viewer, $target, $kind, $at
                WHERE EXISTS (
                    SELECT 1 FROM ImportedChatMessages AS message
                    INNER JOIN ImportedMessageReactionStates AS reaction
                        ON reaction.TelegramUserId = message.TelegramUserId
                       AND reaction.PeerType = message.PeerType
                       AND reaction.PeerId = message.PeerId
                       AND reaction.MessageId = message.MessageId
                    WHERE message.TelegramUserId = $owner AND message.PeerType = $type
                      AND message.PeerId = $peer AND message.MessageId = $message
                      AND reaction.HasOwnReaction = 1
                )
                ON CONFLICT(TelegramUserId, PeerType, PeerId, MessageId) DO UPDATE SET
                    InstagramUserName = excluded.InstagramUserName,
                    TargetUserName = excluded.TargetUserName,
                    VerificationKind = excluded.VerificationKind,
                    VerifiedAtUtc = excluded.VerifiedAtUtc;
                """;
            command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
            command.Parameters.AddWithValue("$type", chat.PeerType);
            command.Parameters.AddWithValue("$peer", chat.PeerId);
            command.Parameters.AddWithValue("$message", messageId);
            command.Parameters.AddWithValue("$viewer", verification.ViewerUserName);
            command.Parameters.AddWithValue("$target", verification.TargetUserName);
            command.Parameters.AddWithValue("$kind", verification.TaskKind.ToString());
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new InvalidOperationException("Можно скрыть только выполненное задание выбранного чата.");
        }
        using (var hide = connection.CreateCommand())
        {
            hide.Transaction = (SqliteTransaction)transaction;
            hide.CommandText = """
                INSERT OR IGNORE INTO ImportedMessageHidden (
                    TelegramUserId, PeerType, PeerId, MessageId)
                VALUES ($owner, $type, $peer, $message)
                """;
            hide.Parameters.AddWithValue("$owner", chat.TelegramUserId);
            hide.Parameters.AddWithValue("$type", chat.PeerType);
            hide.Parameters.AddWithValue("$peer", chat.PeerId);
            hide.Parameters.AddWithValue("$message", messageId);
            await hide.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
