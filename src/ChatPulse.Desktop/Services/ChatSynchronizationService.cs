using System.IO;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Primary message history import with separate safe own-reaction reconciliation.
/// Only completed imports advance the chat cursor; repeated imports use SQLite uniqueness.
/// Task extraction is separate; completed chats can refresh reaction evidence without replacing history.
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
                await SaveAuthorsAsync(current, messages, cancellationToken);
                await SaveReactionStatesAsync(current, messages, cancellationToken);
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

    /// <summary>Explicitly replaces one completed chat's imported snapshot after a successful fetch.</summary>
    public async Task<int> ReloadHistoryAsync(ConfiguredChat configured, int days,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(days));
        ArgumentNullException.ThrowIfNull(configured);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = FindConfigured(await settings.LoadAsync(cancellationToken), configured);
            if (current.SynchronizationState != ChatSynchronizationState.COMPLETED)
                throw new InvalidOperationException("История чата ещё не загружена.");
            if (!ChatTitlePolicy.IsEligible(current.TelegramTitle))
                throw new InvalidOperationException("Название чата не соответствует правилу выбора.");
            progress?.Invoke("Проверяем доступ к Telegram…");
            var owner = await telegram.GetAuthorizedUserIdAsync(cancellationToken);
            if (owner != current.TelegramUserId)
                throw new InvalidOperationException("Telegram-аккаунт изменился.");
            var snapshot = await telegram.GetChatsAsync(cancellationToken);
            var chat = snapshot.Chats.SingleOrDefault(item =>
                item.PeerId == current.PeerId && item.PeerType == current.PeerType);
            if (snapshot.TelegramUserId != owner || chat is null || !ChatTitlePolicy.IsEligible(chat.Title))
                throw new InvalidOperationException("Чат недоступен или не соответствует правилу названия.");
            progress?.Invoke($"Загружаем историю за {days} дн.…");
            var messages = await telegram.GetRecentChatMessagesAsync(chat, days, cancellationToken);
            progress?.Invoke("Заменяем историю чата в SQLite…");
            await ReplaceHistoryAsync(current, messages, cancellationToken);
            await SaveAuthorsAsync(current, messages, cancellationToken);
            await SaveReactionStatesAsync(current, messages, cancellationToken);
            var lastId = messages.Count == 0 ? current.LastProcessedMessageId :
                Math.Max(current.LastProcessedMessageId ?? 0, messages.Max(message => message.MessageId));
            await settings.ConfirmChatHistoryReloadAsync(current.TelegramUserId, current.PeerId,
                current.PeerType, days, messages.Count, lastId, cancellationToken);
            progress?.Invoke("История обновлена.");
            return messages.Count;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Recheck reactions on existing history. Unknown reaction data never resets a saved status.
    /// Does not change message texts, import cursors or synchronization completion markers.
    /// </summary>
    public async Task<int> RefreshReactionsAsync(ConfiguredChat configured,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configured);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = FindConfigured(await settings.LoadAsync(cancellationToken), configured);
            if (current.SynchronizationState != ChatSynchronizationState.COMPLETED)
                throw new InvalidOperationException("Сначала нужно завершить импорт истории чата.");
            var owner = await telegram.GetAuthorizedUserIdAsync(cancellationToken);
            if (owner != current.TelegramUserId)
                throw new InvalidOperationException("Telegram-аккаунт изменился.");
            var snapshot = await telegram.GetChatsAsync(cancellationToken);
            var chat = snapshot.Chats.SingleOrDefault(item => item.PeerId == current.PeerId &&
                item.PeerType == current.PeerType);
            if (snapshot.TelegramUserId != owner || chat is null ||
                !ChatTitlePolicy.IsEligible(chat.Title))
                throw new InvalidOperationException("Чат недоступен для проверки реакций.");
            progress?.Invoke("Проверяем реакции на задания…");
            var messages = await telegram.GetRecentChatMessagesAsync(chat, current.HistoryDays, cancellationToken);
            await SaveAuthorsAsync(current, messages, cancellationToken);
            return await SaveReactionStatesAsync(current, messages, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Metadata is stored separately, keeping the existing history schema intact.
    /// Refresh only updates already-imported message IDs and never changes reaction states.
    /// </summary>
    private async Task SaveAuthorsAsync(ConfiguredChat chat,
        IReadOnlyList<TelegramChatMessage> messages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ImportedMessageAuthors (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL,
                AuthorName TEXT NOT NULL,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = """
            INSERT INTO ImportedMessageAuthors
                (TelegramUserId, PeerType, PeerId, MessageId, AuthorName)
            SELECT $owner, $type, $peer, $id, $author
            WHERE EXISTS (
                SELECT 1 FROM ImportedChatMessages
                WHERE TelegramUserId = $owner AND PeerType = $type
                  AND PeerId = $peer AND MessageId = $id
            )
            ON CONFLICT(TelegramUserId, PeerType, PeerId, MessageId)
            DO UPDATE SET AuthorName = excluded.AuthorName
            """;
        save.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        save.Parameters.AddWithValue("$type", chat.PeerType);
        save.Parameters.AddWithValue("$peer", chat.PeerId);
        var id = save.Parameters.Add("$id", SqliteType.Integer);
        var author = save.Parameters.Add("$author", SqliteType.Text);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(message.AuthorName)) continue;
            id.Value = message.MessageId;
            author.Value = message.AuthorName;
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<int> SaveReactionStatesAsync(ConfiguredChat chat,
        IReadOnlyList<TelegramChatMessage> messages, CancellationToken cancellationToken)
    {
        if (!messages.Any(message => message.HasOwnReaction.HasValue)) return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            INSERT INTO ImportedMessageReactionStates
                (TelegramUserId, PeerType, PeerId, MessageId, HasOwnReaction)
            SELECT $owner, $peerType, $peerId, $id, $reaction
            WHERE EXISTS (
                SELECT 1 FROM ImportedChatMessages
                WHERE TelegramUserId = $owner AND PeerType = $peerType
                  AND PeerId = $peerId AND MessageId = $id
            )
            ON CONFLICT(TelegramUserId, PeerType, PeerId, MessageId)
            DO UPDATE SET HasOwnReaction = excluded.HasOwnReaction
            """;
        update.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        update.Parameters.AddWithValue("$peerType", chat.PeerType);
        update.Parameters.AddWithValue("$peerId", chat.PeerId);
        var id = update.Parameters.Add("$id", SqliteType.Integer);
        var reaction = update.Parameters.Add("$reaction", SqliteType.Integer);
        var total = 0;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!message.HasOwnReaction.HasValue) continue;
            id.Value = message.MessageId;
            reaction.Value = message.HasOwnReaction.Value ? 1 : 0;
            total += await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return total;
    }

    private async Task ReplaceHistoryAsync(ConfiguredChat chat,
        IReadOnlyList<TelegramChatMessage> messages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ImportedChatMessages (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL, Text TEXT,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM ImportedChatMessages
            WHERE TelegramUserId = $owner AND PeerType = $peerType AND PeerId = $peerId
            """;
        delete.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        delete.Parameters.AddWithValue("$peerType", chat.PeerType);
        delete.Parameters.AddWithValue("$peerId", chat.PeerId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
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
        var id = insert.Parameters.Add("$messageId", SqliteType.Integer);
        var body = insert.Parameters.Add("$text", SqliteType.Text);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = message.MessageId;
            body.Value = message.Text is null ? DBNull.Value : message.Text;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
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
