using System.IO;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Publishes a completed external verification as one SQLite transaction.
/// Physical deletion is intentionally not part of this operation.
/// </summary>
public sealed class WorkspaceTaskVerificationStore(string databasePath)
{
    public async Task PublishAsync(ConfiguredChat chat, long messageId,
        InstagramTaskVerificationResult verification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(verification);
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        if (!verification.CanHide)
            throw new InvalidOperationException("Проверка не завершена: задание нельзя скрывать.");
        if (!File.Exists(databasePath))
            throw new InvalidOperationException("История Telegram ещё не загружена.");

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        using (var setup = connection.CreateCommand())
        {
            setup.Transaction = (SqliteTransaction)transaction;
            setup.CommandText = """
                CREATE TABLE IF NOT EXISTS ImportedMessageVerified (
                    TelegramUserId INTEGER NOT NULL,
                    PeerType TEXT NOT NULL,
                    PeerId INTEGER NOT NULL,
                    MessageId INTEGER NOT NULL,
                    InstagramUserName TEXT NOT NULL,
                    PostUrl TEXT NOT NULL,
                    VerifiedLike INTEGER NOT NULL CHECK (VerifiedLike IN (0, 1)),
                    VerifiedComment INTEGER NOT NULL CHECK (VerifiedComment IN (0, 1)),
                    VerifiedSave INTEGER NOT NULL DEFAULT 0 CHECK (VerifiedSave IN (0, 1)),
                    VerifiedRepost INTEGER NOT NULL DEFAULT 0 CHECK (VerifiedRepost IN (0, 1)),
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
            await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Existing installs may already have the two-check verification table.
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = (SqliteTransaction)transaction;
            schema.CommandText = "PRAGMA table_info(ImportedMessageVerified)";
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = await schema.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    columns.Add(reader.GetString(1));
            foreach (var column in new[] { "VerifiedSave", "VerifiedRepost" })
            {
                if (columns.Contains(column)) continue;
                using var alter = connection.CreateCommand();
                alter.Transaction = (SqliteTransaction)transaction;
                alter.CommandText = $"ALTER TABLE ImportedMessageVerified ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0 CHECK ({column} IN (0, 1))";
                await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO ImportedMessageVerified
                    (TelegramUserId, PeerType, PeerId, MessageId, InstagramUserName,
                     PostUrl, VerifiedLike, VerifiedComment, VerifiedSave, VerifiedRepost, VerifiedAtUtc)
                SELECT $owner, $type, $peer, $message, $user, $url, $like, $comment, $save, $repost, $at
                WHERE EXISTS (
                    SELECT 1 FROM ImportedChatMessages AS message
                    INNER JOIN ImportedMessageReactionStates AS reaction
                        ON reaction.TelegramUserId = message.TelegramUserId
                       AND reaction.PeerType = message.PeerType
                       AND reaction.PeerId = message.PeerId
                       AND reaction.MessageId = message.MessageId
                    WHERE message.TelegramUserId = $owner
                      AND message.PeerType = $type AND message.PeerId = $peer
                      AND message.MessageId = $message AND reaction.HasOwnReaction = 1
                )
                ON CONFLICT(TelegramUserId, PeerType, PeerId, MessageId) DO UPDATE SET
                    InstagramUserName = excluded.InstagramUserName,
                    PostUrl = excluded.PostUrl,
                    VerifiedLike = excluded.VerifiedLike,
                    VerifiedComment = excluded.VerifiedComment,
                    VerifiedSave = excluded.VerifiedSave,
                    VerifiedRepost = excluded.VerifiedRepost,
                    VerifiedAtUtc = excluded.VerifiedAtUtc;
                """;
            command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
            command.Parameters.AddWithValue("$type", chat.PeerType);
            command.Parameters.AddWithValue("$peer", chat.PeerId);
            command.Parameters.AddWithValue("$message", messageId);
            command.Parameters.AddWithValue("$user", verification.UserName);
            command.Parameters.AddWithValue("$url", verification.PostUrl);
            command.Parameters.AddWithValue("$like", verification.Like is null ? 0 : 1);
            command.Parameters.AddWithValue("$comment", verification.Comment is null ? 0 : 1);
            command.Parameters.AddWithValue("$save", verification.Save is null ? 0 : 1);
            command.Parameters.AddWithValue("$repost", verification.Repost is null ? 0 : 1);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new InvalidOperationException("Можно проверить только выполненное задание выбранного чата.");
        }

        using (var hide = connection.CreateCommand())
        {
            hide.Transaction = (SqliteTransaction)transaction;
            hide.CommandText = """
                INSERT OR IGNORE INTO ImportedMessageHidden
                    (TelegramUserId, PeerType, PeerId, MessageId)
                VALUES ($owner, $type, $peer, $message)
                """;
            hide.Parameters.AddWithValue("$owner", chat.TelegramUserId);
            hide.Parameters.AddWithValue("$type", chat.PeerType);
            hide.Parameters.AddWithValue("$peer", chat.PeerId);
            hide.Parameters.AddWithValue("$message", messageId);
            await hide.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
