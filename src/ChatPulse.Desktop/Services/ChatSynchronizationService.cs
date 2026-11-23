using System.IO;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

public sealed record ParticipantListRefreshResult(long? SourceMessageId, bool Applied,
    DateTime? SourceMessageSentAtUtc = null,
    ParticipantListApplyStatus Status = ParticipantListApplyStatus.NO_VALID_LIST,
    ParticipantListSourceInfo? StoredSource = null,
    string? ParseFailureReason = null,
    int ProcessedListCount = 0)
{
    public string OutcomeTitle => Status switch
    {
        ParticipantListApplyStatus.APPLIED => "Список обновлён",
        ParticipantListApplyStatus.ALREADY_CURRENT => "Список уже актуален",
        ParticipantListApplyStatus.OLDER_MESSAGE => "Найден более старый список",
        ParticipantListApplyStatus.AMBIGUOUS_MATCH => "Список не применён",
        ParticipantListApplyStatus.PROFILE_CHECK_INCONCLUSIVE => "Профиль не подтверждён",
        ParticipantListApplyStatus.UNPARSEABLE_MESSAGE => "Последний список не разобран",
        _ => "Список не найден"
    };

    public string OutcomeDescription
    {
        get
        {
            if (SourceMessageId is null)
                return "Список участниц в истории не найден. Предыдущий список сохранён.";
            var date = SourceMessageSentAtUtc is { } timestamp
                ? new ParticipantListSourceInfo(SourceMessageId.Value, timestamp).DateLabel
                : "дата неизвестна";
            var found = $"Найдено сообщение Telegram №{SourceMessageId} от {date}.";
            var saved = StoredSource is { } source
                ? $" Применённый список: сообщение №{source.MessageId}, {source.DateLabel}."
                : " Ранее применённого списка нет.";
            return Status switch
            {
                ParticipantListApplyStatus.APPLIED =>
                    found + (ProcessedListCount > 1
                        ? $" Применено {ProcessedListCount} новых списков в порядке публикации. История и реестр обновлены."
                        : " Список применён к реестру и истории участниц."),
                ParticipantListApplyStatus.ALREADY_CURRENT =>
                    SourceMessageSentAtUtc.HasValue
                        ? $"Список от {date} уже сохранён. Изменений нет."
                        : "Список уже сохранён. Изменений нет.",
                ParticipantListApplyStatus.OLDER_MESSAGE =>
                    found + " Сообщение старее сохранённого, поэтому список не заменён." + saved,
                ParticipantListApplyStatus.AMBIGUOUS_MATCH =>
                    found + " Список не применён: сопоставление участниц по Instagram-профилям неоднозначно. " +
                    "Предыдущий список сохранён." + saved,
                ParticipantListApplyStatus.PROFILE_CHECK_INCONCLUSIVE =>
                    found + " Список не применён: не удалось надёжно подтвердить страницу участницы " +
                    "из раздела «ШТРАФНЫЕ ДНИ». Предыдущий список сохранён." + saved,
                ParticipantListApplyStatus.UNPARSEABLE_MESSAGE =>
                    found + " Последнее сообщение со списком участниц не удалось разобрать. " +
                    (string.IsNullOrWhiteSpace(ParseFailureReason)
                        ? "Причина не определена; проверьте ссылки и формат записей. "
                        : $"Причина: {ParseFailureReason} ") +
                    "Предыдущий список сохранён." + saved,
                _ => found + " Список не применён." + saved
            };
        }
    }
}

public enum ParticipantListSyncStage
{
    SETTINGS,
    TELEGRAM,
    SEARCH,
    SAVE,
    DISPLAY
}

public sealed record ParticipantListSyncProgress(
    ParticipantListSyncStage Stage, string Description,
    int PagesScanned = 0, int MessagesScanned = 0, DateTime? SourceMessageSentAtUtc = null);

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

    /// <summary>Only a successful Telegram RPC may mark a task completed locally.</summary>
    public async Task CompleteTaskAsync(ConfiguredChat configured, long messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if (messageId <= 0 || messageId > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(messageId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = FindConfigured(await settings.LoadAsync(cancellationToken), configured);
            if (current.SynchronizationState != ChatSynchronizationState.COMPLETED)
                throw new InvalidOperationException("История чата ещё не загружена.");
            var store = new WorkspaceHeartReactionStore(databasePath);
            if (!await store.RequiresReactionAsync(current, messageId, cancellationToken)) return;

            var owner = await telegram.GetAuthorizedUserIdAsync(cancellationToken);
            if (owner != current.TelegramUserId)
                throw new InvalidOperationException("Telegram-аккаунт изменился.");
            var snapshot = await telegram.GetChatsAsync(cancellationToken);
            var chat = snapshot.Chats.SingleOrDefault(item => item.PeerId == current.PeerId &&
                item.PeerType == current.PeerType);
            if (snapshot.TelegramUserId != owner || chat is null ||
                !ChatTitlePolicy.IsEligible(chat.Title))
                throw new InvalidOperationException("Чат недоступен для отправки реакции.");
            if (telegram is not ITelegramTaskReactionSender sender)
                throw new InvalidOperationException("Отправка реакций недоступна в Telegram-клиенте.");

            await sender.SendHeartReactionAsync(chat, checked((int)messageId), cancellationToken);
            // The remote action cannot be rolled back if local persistence fails.
            try
            {
                await store.ConfirmAsync(current, messageId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Сердечко отправлено в Telegram, но статус не сохранился в SQLite. " +
                    "Проверьте реакции через настройки, прежде чем повторять действие.", exception);
            }
        }
        finally { _gate.Release(); }
    }

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
            await SaveMessageDatesAsync(current, messages, cancellationToken);
                await SaveCustomEmojiAsync(current, messages, cancellationToken);
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

    /// <summary>
    /// Imports messages after the latest saved positive own reaction (or the import cursor
    /// when there is no such reaction). Previously imported messages stay unchanged.
    /// A separate recent-history pass refreshes reactions and metadata without removing
    /// older rows, including manually skipped tasks.
    /// </summary>
    public async Task<int> SynchronizeLatestAsync(ConfiguredChat configured,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configured);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = FindConfigured(await settings.LoadAsync(cancellationToken), configured);
            if (current.SynchronizationState != ChatSynchronizationState.COMPLETED)
                throw new InvalidOperationException("Сначала нужно завершить первоначальный импорт.");
            var owner = await telegram.GetAuthorizedUserIdAsync(cancellationToken);
            if (owner != current.TelegramUserId)
                throw new InvalidOperationException("Telegram-аккаунт изменился.");
            var snapshot = await telegram.GetChatsAsync(cancellationToken);
            var chat = snapshot.Chats.SingleOrDefault(item =>
                item.PeerId == current.PeerId && item.PeerType == current.PeerType);
            if (snapshot.TelegramUserId != owner || chat is null ||
                !ChatTitlePolicy.IsEligible(chat.Title))
                throw new InvalidOperationException("Чат недоступен для синхронизации.");
            if (telegram is not ITelegramIncrementalMessageReader incremental)
                throw new InvalidOperationException("Telegram-клиент не поддерживает загрузку новых сообщений.");

            var reactedId = await GetLatestOwnReactionIdAsync(current, cancellationToken);
            // A positive reaction is an overlap anchor, never a reason to discard older
            // pending tasks. Without one, the import cursor is the safe incremental anchor.
            var anchor = reactedId ?? current.LastProcessedMessageId ?? 0;
            if (anchor > int.MaxValue)
                throw new InvalidOperationException("Недопустимый номер сообщения Telegram.");
            progress?.Invoke("Получаем новые сообщения из Telegram…");
            var fetched = await incremental.GetChatMessagesAfterAsync(chat, (int)anchor, cancellationToken);
            progress?.Invoke($"Получено сообщений: {fetched.Count}. Сохраняем историю в SQLite…");
            await SaveHistoryAsync(current, fetched, cancellationToken);
            await SaveAuthorsAsync(current, fetched, cancellationToken);
            await SaveMessageDatesAsync(current, fetched, cancellationToken);
            await SaveCustomEmojiAsync(current, fetched, cancellationToken);
            await SaveReactionStatesAsync(current, fetched, cancellationToken);

            // Recheck recent older tasks too: their reaction may have changed after import.
            // The recent window is metadata-only; it must never replace original texts.
            progress?.Invoke("Повторно проверяем реакции и участников…");
            var recent = await telegram.GetRecentChatMessagesAsync(chat, current.HistoryDays, cancellationToken);
            progress?.Invoke($"Получено для перепроверки: {recent.Count}. Сохраняем реакции и данные участников…");
            await SaveAuthorsAsync(current, recent, cancellationToken);
            await SaveMessageDatesAsync(current, recent, cancellationToken);
            await SaveCustomEmojiAsync(current, recent, cancellationToken);
            await SaveReactionStatesAsync(current, recent, cancellationToken);

            var total = await CountImportedMessagesAsync(current, cancellationToken);
            var lastId = fetched.Count == 0 ? current.LastProcessedMessageId :
                Math.Max(current.LastProcessedMessageId ?? 0, fetched.Max(message => (long)message.MessageId));
            await settings.UpdateChatSyncAsync(current.TelegramUserId, current.PeerId, current.PeerType,
                ChatSynchronizationState.COMPLETED, total, lastId, null, cancellationToken);
            progress?.Invoke($"Синхронизация завершена. Новых сообщений: {Math.Max(0, total - current.ImportedMessageCount)}.");
            return Math.Max(0, total - current.ImportedMessageCount);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Atomically refreshes the selected chat's participant register and history.
    /// Every list after the last committed message ID is checked in publication
    /// order. The task import cursor and Instagram checks are not modified.
    /// </summary>
    public async Task<ParticipantListRefreshResult> RefreshLatestParticipantListAsync(
        ConfiguredChat configured, Action<ParticipantListSyncProgress>? progress = null,
        CancellationToken cancellationToken = default, Func<string, bool?>? profileOpens = null)
    {
        ArgumentNullException.ThrowIfNull(configured);
        progress?.Invoke(new(ParticipantListSyncStage.SETTINGS, "Проверяем настройки выбранного чата…"));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var syncSettings = await settings.LoadAsync(cancellationToken);
            var current = FindConfigured(syncSettings, configured);
            if (current.SynchronizationState != ChatSynchronizationState.COMPLETED)
                throw new InvalidOperationException("Сначала завершите первоначальную синхронизацию чата.");
            progress?.Invoke(new(ParticipantListSyncStage.TELEGRAM,
                "Проверяем Telegram-аккаунт и доступ к чату…"));
            var owner = await telegram.GetAuthorizedUserIdAsync(cancellationToken);
            if (owner != current.TelegramUserId)
                throw new InvalidOperationException("Telegram-аккаунт изменился.");
            var chats = await telegram.GetChatsAsync(cancellationToken);
            var chat = chats.Chats.SingleOrDefault(item => item.PeerId == current.PeerId &&
                item.PeerType == current.PeerType);
            if (chats.TelegramUserId != owner || chat is null ||
                !ChatTitlePolicy.IsEligible(chat.Title))
                throw new InvalidOperationException("Чат недоступен для синхронизации списка.");
            if (telegram is not ITelegramParticipantListHistoryReader reader)
                throw new InvalidOperationException(
                    "Telegram-клиент не поддерживает чтение всех новых списков участниц.");

            var registry = new ChatParticipantRegistry(databasePath);
            var previous = await registry.ReadLatestSourceAsync(current, cancellationToken);
            var parser = ChatParticipantHistoryRuleSelection.SupportsChat14(current)
                ? (IChatParticipantHistoryParser)new Chat14ParticipantHistoryParser()
                : new StandardParticipantHistoryParser(
                    current.SocialNetwork.Equals("Instagram", StringComparison.OrdinalIgnoreCase));
            var telegramChat = new TelegramChatOption(current.PeerId, current.PeerType,
                current.TelegramTitle, current.PeerType == "Chat" ? "Группа" : "Канал");
            progress?.Invoke(new(ParticipantListSyncStage.SEARCH,
                "Получаем все списки участниц, опубликованные после предыдущей синхронизации…"));
            // The reader guarantees complete traversal (throws instead of returning
            // an incomplete result). The task-import cursor is not consulted.
            var found = await reader.FindMatchingMessagesAfterAsync(telegramChat,
                previous?.MessageId ?? 0, parser.IsCandidate, cancellationToken,
                (pages, messages, count) => progress?.Invoke(new(ParticipantListSyncStage.SEARCH,
                    $"Просмотрено страниц: {pages} · сообщений: {messages} · списков: {count}",
                    pages, messages)));
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = found.OrderBy(item => item.MessageId).ToArray();
            if (candidates.Length == 0)
            {
                var noChange = previous is not null;
                var result = new ParticipantListRefreshResult(previous?.MessageId, false,
                    previous?.SentAtUtc, noChange ? ParticipantListApplyStatus.ALREADY_CURRENT
                        : ParticipantListApplyStatus.NO_VALID_LIST, previous);
                await registry.SaveLastAttemptAsync(current, previous?.MessageId,
                    previous?.SentAtUtc, result.Status, result.OutcomeDescription,
                    CancellationToken.None);
                return result;
            }
            var newest = candidates[^1];
            var pending = candidates;

            var dailyStore = new ChatParticipantDailySyncStore(databasePath);
            var known = new HashSet<string>(
                await dailyStore.ReadKnownUsernamesAsync(current, cancellationToken),
                StringComparer.OrdinalIgnoreCase);
            var verified = new List<ParticipantHistoryCandidate>(pending.Length);
            foreach (var item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(new(ParticipantListSyncStage.SEARCH,
                    $"Проверяем список Telegram №{item.MessageId}…",
                    SourceMessageSentAtUtc: item.SentAtUtc));
                Func<string, CancellationToken, Task<bool?>>? browserCheck = profileOpens is null
                    ? null : (url, _) => Task.FromResult(profileOpens(url));
                var parsed = profileOpens is null
                    ? await parser.ParseAsync(item.Text, item.SentAtUtc, known,
                        browserCheck, cancellationToken)
                    : await Task.Run(() => parser.ParseAsync(item.Text, item.SentAtUtc,
                        known, browserCheck, cancellationToken), cancellationToken);
                var candidate = new ParticipantHistoryCandidate(item.MessageId, item.SentAtUtc,
                    item.Text ?? string.Empty, parsed.Entries, parsed.Error);
                string? reason = parsed.Error;
                if (parsed.IsSuccess)
                {
                    try { ChatParticipantHistoryStore.VerifiedUniqueEntries(candidate); }
                    catch (InvalidOperationException exception) { reason = exception.Message; }
                }
                if (!parsed.IsSuccess || reason is not null)
                {
                    var failed = new ParticipantListRefreshResult(item.MessageId, false,
                        item.SentAtUtc, ParticipantListApplyStatus.UNPARSEABLE_MESSAGE,
                        previous, $"Сообщение №{item.MessageId}: {reason ?? "не удалось разобрать список"}. " +
                        $"Все {pending.Length} новых списков оставлены без изменений.");
                    await registry.SaveLastAttemptAsync(current, item.MessageId,
                        item.SentAtUtc, failed.Status, failed.OutcomeDescription,
                        CancellationToken.None);
                    return failed;
                }
                verified.Add(candidate);
                foreach (var entry in parsed.Entries!)
                {
                    var username = new Uri(entry.SubscriptionProfileUrl).AbsolutePath.Trim('/');
                    known.Add(username);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new(ParticipantListSyncStage.SAVE,
                $"Сохраняем {verified.Count} новых списков одной транзакцией…",
                SourceMessageSentAtUtc: newest.SentAtUtc));
            var successful = new ParticipantListRefreshResult(newest.MessageId, true,
                newest.SentAtUtc, ParticipantListApplyStatus.APPLIED,
                ProcessedListCount: verified.Count);
            await dailyStore.CommitAsync(current, verified, parser.Rule,
                previous?.MessageId, successful.OutcomeDescription, cancellationToken,
                syncSettings.Chats, syncSettings.InstagramTargetUserName,
                syncSettings.InstagramBrowserUserName);
            var stored = await registry.ReadLatestSourceAsync(current, CancellationToken.None);
            return successful with { StoredSource = stored };
        }
        finally { _gate.Release(); }
    }

    private async Task<long?> GetLatestOwnReactionIdAsync(ConfiguredChat chat,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath)) return null;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportedMessageReactionStates'";
        if (Convert.ToInt64(await table.ExecuteScalarAsync(cancellationToken)) == 0) return null;
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT MAX(r.MessageId)
            FROM ImportedMessageReactionStates r
            INNER JOIN ImportedChatMessages m
              ON m.TelegramUserId = r.TelegramUserId AND m.PeerType = r.PeerType
             AND m.PeerId = r.PeerId AND m.MessageId = r.MessageId
            WHERE r.TelegramUserId = $owner AND r.PeerType = $type
              AND r.PeerId = $peer AND r.HasOwnReaction = 1
            """;
        query.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        query.Parameters.AddWithValue("$type", chat.PeerType);
        query.Parameters.AddWithValue("$peer", chat.PeerId);
        var result = await query.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private async Task<int> CountImportedMessagesAsync(ConfiguredChat chat,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT COUNT(*) FROM ImportedChatMessages
            WHERE TelegramUserId = $owner AND PeerType = $type AND PeerId = $peer
            """;
        query.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        query.Parameters.AddWithValue("$type", chat.PeerType);
        query.Parameters.AddWithValue("$peer", chat.PeerId);
        return Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken));
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
            await SaveMessageDatesAsync(current, messages, cancellationToken);
                await SaveCustomEmojiAsync(current, messages, cancellationToken);
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
            await SaveMessageDatesAsync(current, messages, cancellationToken);
            await SaveCustomEmojiAsync(current, messages, cancellationToken);
            return await SaveReactionStatesAsync(current, messages, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Metadata is stored separately, keeping the existing history schema intact.
    /// Refresh only updates already-imported message IDs and never changes reaction states.
    /// </summary>
    /// <summary>Stores entity offsets separately without rewriting the original message.</summary>
    /// <summary>
    /// Stores the original Telegram publication timestamp independently of message text.
    /// Older history is populated on the next explicit Telegram refresh; unknown dates stay unknown.
    /// </summary>
    private async Task SaveMessageDatesAsync(ConfiguredChat chat,
        IReadOnlyList<TelegramChatMessage> messages, CancellationToken cancellationToken)
    {
        if (!messages.Any(message => message.SentAtUtc.HasValue)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ImportedMessageDates (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL,
                SentAtUnixSeconds INTEGER NOT NULL,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO ImportedMessageDates
                (TelegramUserId, PeerType, PeerId, MessageId, SentAtUnixSeconds)
            SELECT $owner, $type, $peer, $id, $timestamp
            WHERE EXISTS (
                SELECT 1 FROM ImportedChatMessages
                WHERE TelegramUserId = $owner AND PeerType = $type
                  AND PeerId = $peer AND MessageId = $id
            )
            ON CONFLICT(TelegramUserId, PeerType, PeerId, MessageId)
            DO UPDATE SET SentAtUnixSeconds = excluded.SentAtUnixSeconds
            """;
        upsert.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        upsert.Parameters.AddWithValue("$type", chat.PeerType);
        upsert.Parameters.AddWithValue("$peer", chat.PeerId);
        var id = upsert.Parameters.Add("$id", SqliteType.Integer);
        var timestamp = upsert.Parameters.Add("$timestamp", SqliteType.Integer);
        foreach (var message in messages)
        {
            if (message.SentAtUtc is not { } sentAt) continue;
            id.Value = message.MessageId;
            timestamp.Value = new DateTimeOffset(sentAt.ToUniversalTime()).ToUnixTimeSeconds();
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SaveCustomEmojiAsync(ConfiguredChat chat,
        IReadOnlyList<TelegramChatMessage> messages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ImportedMessageCustomEmoji (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL,
                EntitiesJson TEXT NOT NULL,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = """
            INSERT INTO ImportedMessageCustomEmoji
                (TelegramUserId, PeerType, PeerId, MessageId, EntitiesJson)
            SELECT $owner, $type, $peer, $id, $json
            WHERE EXISTS (
                SELECT 1 FROM ImportedChatMessages
                WHERE TelegramUserId = $owner AND PeerType = $type
                  AND PeerId = $peer AND MessageId = $id
            )
            ON CONFLICT(TelegramUserId, PeerType, PeerId, MessageId)
            DO UPDATE SET EntitiesJson = excluded.EntitiesJson
            """;
        save.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        save.Parameters.AddWithValue("$type", chat.PeerType);
        save.Parameters.AddWithValue("$peer", chat.PeerId);
        var id = save.Parameters.Add("$id", SqliteType.Integer);
        var json = save.Parameters.Add("$json", SqliteType.Text);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.CustomEmojis is null) continue; // old/test API responses: do not erase evidence
            id.Value = message.MessageId;
            json.Value = System.Text.Json.JsonSerializer.Serialize(message.CustomEmojis);
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SaveAuthorsAsync(ConfiguredChat chat,
        IReadOnlyList<TelegramChatMessage> messages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var setup = connection.CreateCommand();
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
        using var save = connection.CreateCommand();
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
            if (string.IsNullOrWhiteSpace(message.AuthorName) ||
                string.Equals(message.AuthorName, "Автор неизвестен", StringComparison.OrdinalIgnoreCase))
                continue;
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
        using var setup = connection.CreateCommand();
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
        using var update = connection.CreateCommand();
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
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ImportedChatMessages (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL, Text TEXT,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM ImportedChatMessages
            WHERE TelegramUserId = $owner AND PeerType = $peerType AND PeerId = $peerId
            """;
        delete.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        delete.Parameters.AddWithValue("$peerType", chat.PeerType);
        delete.Parameters.AddWithValue("$peerId", chat.PeerId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
        using var insert = connection.CreateCommand();
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
        using var setup = connection.CreateCommand();
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
        using var insert = connection.CreateCommand();
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
