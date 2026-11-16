using System.IO;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Primary message history import. A completed chat is never imported again by this workflow.
/// Only completed imports advance the chat cursor; repeated imports use SQLite uniqueness.
/// Task extraction is deliberately NOT part of this service yet.
/// </summary>
public sealed class ChatSynchronizationService(
    ISettingsService settings,
    ITelegramAuthenticationClient telegram,
    string databasePath)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<long> GetAccountIdAsync(CancellationToken cancellationToken = default)
        => telegram.GetAuthorizedUserIdAsync(cancellationToken);

    public async Task<int> SynchronizeAsync(ConfiguredChat configured,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if (configured.TelegramUserId <= 0 || configured.HistoryDays is < 1 or > 30 ||
            !ChatTitlePolicy.IsEligible(configured.TelegramTitle))
            throw new InvalidOperationException("Некорректная конфигурация синхронизации чата.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = FindConfigured(await settings.LoadAsync(cancellationToken), configured);
            if (current.SynchronizationState == ChatSynchronizationState.COMPLETED)
                return current.ImportedMessageCount;

            var count = current.ImportedMessageCount;
            var lastId = current.LastProcessedMessageId;
            await settings.UpdateChatSyncAsync(current.TelegramUserId, current.PeerId, current.PeerType,
                ChatSynchronizationState.RUNNING, count, lastId, null, cancellationToken);
            try
            {
                progress?.Invoke("Проверяем Telegram-аккаунт и доступ к чату…");
                var actualOwner = await telegram.GetAuthorizedUserIdAsync(cancellationToken);
                if (actualOwner != current.TelegramUserId)
                    throw new InvalidOperationException("Telegram-аккаунт изменился.");

                var snapshot = await telegram.GetChatsAsync(cancellationToken);
                var chat = snapshot.Chats.SingleOrDefault(item =>
                    item.PeerId == current.PeerId && item.PeerType == current.PeerType);
                if (snapshot.TelegramUserId != actualOwner || chat is null ||
                    !ChatTitlePolicy.IsEligible(chat.Title))
                    throw new InvalidOperationException("Чат недоступен или не соответствует правилу названия.");

                progress?.Invoke($"Загружаем историю за {current.HistoryDays} дн.…");
                var messages = await telegram.GetRecentChatMessagesAsync(chat, current.HistoryDays, cancellationToken);

                progress?.Invoke("Сохраняем сообщения в SQLite…");
                await SaveHistoryAsync(current, messages, cancellationToken);
                // SQLite is committed before the success flag. Crash/retry is safe because
                // UNIQUE (account, peer type, peer id, message id) removes duplicates.
                count = messages.Count;
                lastId = messages.Count == 0 ? lastId : messages.Max(message => message.MessageId);
                await settings.UpdateChatSyncAsync(current.TelegramUserId, current.PeerId, current.PeerType,
                    ChatSynchronizationState.COMPLETED, count, lastId, null, cancellationToken);
                progress?.Invoke("История загружена.");
                return count;
            }
            catch (OperationCanceledException)
            {
                // Cancellation preserves the Running marker, which is recoverable on restart.
                throw;
            }
            catch (Exception exception)
            {
                await settings.UpdateChatSyncAsync(current.TelegramUserId, current.PeerId, current.PeerType,
                    ChatSynchronizationState.FAILED, count, lastId, exception.Message, CancellationToken.None);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private static ConfiguredChat FindConfigured(AppSettings appSettings, ConfiguredChat expected)
    {
        return appSettings.Chats.Single(chat => chat.TelegramUserId == expected.TelegramUserId &&
            chat.PeerId == expected.PeerId && chat.PeerType == expected.PeerType);
    }

    private async Task SaveHistoryAsync(ConfiguredChat chat, IReadOnlyList<TelegramChatMessage> messages,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ImportedChatMessages (
                TelegramUserId INTEGER NOT NULL,
                PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL,
                Text TEXT,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO ImportedChatMessages
                (TelegramUserId, PeerType, PeerId, MessageId, Text)
            VALUES ($owner, $peerType, $peerId, $messageId, $text)
            """;
        insert.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        insert.Parameters.AddWithValue("$peerType", chat.PeerType);
        insert.Parameters.AddWithValue("$peerId", chat.PeerId);
        var messageId = insert.Parameters.Add("$messageId", SqliteType.Integer);
        var text = insert.Parameters.Add("$text", SqliteType.Text);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            messageId.Value = message.MessageId;
            text.Value = message.Text is null ? DBNull.Value : message.Text;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
