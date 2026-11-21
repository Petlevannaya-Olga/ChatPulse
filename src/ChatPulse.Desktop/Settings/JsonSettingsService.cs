using System.IO;
using ChatPulse.Contracts.Telegram;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace ChatPulse.Desktop.Settings;

/// <summary>
/// Один централизованный settings.json по образцу Mnemora.
/// Атомарная замена файла и SemaphoreSlim не дают параллельным обновлениям
/// терять настройки друг друга.
/// </summary>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;
    private readonly string _directory;
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonSettingsService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatPulse")) { }

    // Позволяет проверить работу хранилища в изолированной временной папке.
    public JsonSettingsService(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _path = Path.Combine(_directory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken);
        try
        {
            return await LoadInternalAsync(cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public Task SaveInstagramAccountsAsync(string target, string browser,
        CancellationToken cancellationToken = default)
    {
        var targetName = InstagramUserName.Normalize(target);
        var browserName = InstagramUserName.Normalize(browser);
        return UpdateAsync(settings =>
        {
            settings.InstagramTargetUserName = targetName;
            settings.InstagramBrowserUserName = browserName;
        }, cancellationToken);
    }

    public Task SaveSubscriptionColumnWidthsAsync(IReadOnlyDictionary<string, double> widths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(widths);
        foreach (var (key, width) in widths)
        {
            if (string.IsNullOrWhiteSpace(key) || !double.IsFinite(width) || width is < 1 or > 4000)
                throw new ArgumentException("Некорректные ширины столбцов.", nameof(widths));
        }

        // Store only the UI preference. Atomic update preserves all unrelated settings.
        var snapshot = new Dictionary<string, double>(widths, StringComparer.Ordinal);
        return UpdateAsync(settings => settings.SubscriptionColumnWidths = snapshot, cancellationToken);
    }

    public Task SaveOnboardingStepAsync(
        OnboardingStep step, CancellationToken cancellationToken = default)
        => UpdateAsync(settings => settings.CurrentOnboardingStep = step, cancellationToken);

    public Task SaveUserNameAndStepAsync(
        string userName, OnboardingStep step, CancellationToken cancellationToken = default)
        => UpdateAsync(settings =>
        {
            settings.UserName = userName.Trim();
            settings.CurrentOnboardingStep = step;
            if (step == OnboardingStep.Telegram)
                settings.CurrentTelegramPage = TelegramOnboardingPage.ApiCredentials;
        }, cancellationToken);

    public Task SaveTelegramPageAsync(
        TelegramOnboardingPage page, CancellationToken cancellationToken = default)
        => UpdateAsync(settings => settings.CurrentTelegramPage = page, cancellationToken);

    public Task CompleteOnboardingAsync(CancellationToken cancellationToken = default)
        => UpdateAsync(settings =>
        {
            if (settings.PendingChats?.Count > 0 || settings.Chats.Count == 0 ||
                settings.Chats.Any(chat => chat.SynchronizationState != ChatSynchronizationState.COMPLETED ||
                    chat.RequestedHistoryDays.HasValue && chat.RequestedHistoryDays != chat.HistoryDays))
                throw new InvalidOperationException(
                    "Онбординг нельзя завершить до синхронизации всех чатов.");
            settings.CurrentOnboardingStep = OnboardingStep.Workspace;
            settings.IsOnboardingCompleted = true;
        }, cancellationToken);

    public Task SavePendingChatsAsync(long owner, IReadOnlyList<PendingChatConnection> chats,
        CancellationToken cancellationToken = default)
    {
        if (owner <= 0 || chats.Count == 0 || chats.Any(chat =>
                chat.TelegramUserId != owner || chat.PeerId <= 0 ||
                string.IsNullOrWhiteSpace(chat.PeerType) || !ChatTitlePolicy.IsEligible(chat.TelegramTitle) ||
                chat.SocialNetwork is not ("Instagram" or "Telegram") || chat.HistoryDays is < 1 or > 30))
            throw new ArgumentException("Недопустимый список чатов для подключения.", nameof(chats));
        if (chats.Select(chat => (chat.PeerId, chat.PeerType)).Distinct().Count() != chats.Count)
            throw new ArgumentException("Повторяющиеся чаты.", nameof(chats));
        return UpdateAsync(settings =>
        {
            settings.PendingChats ??= [];
            var previous = settings.PendingChats.Where(chat => chat.TelegramUserId == owner)
                .ToDictionary(chat => (chat.PeerId, chat.PeerType));
            settings.PendingChats.RemoveAll(chat => chat.TelegramUserId == owner);
            foreach (var chat in chats)
            {
                var pending = new PendingChatConnection
                {
                    TelegramUserId = owner, PeerId = chat.PeerId, PeerType = chat.PeerType,
                    TelegramTitle = chat.TelegramTitle, SocialNetwork = chat.SocialNetwork,
                    HistoryDays = previous.TryGetValue((chat.PeerId, chat.PeerType), out var prior)
                        ? prior.HistoryDays : chat.HistoryDays
                };
                if (!settings.Chats.Any(saved => saved.TelegramUserId == owner &&
                    saved.PeerId == pending.PeerId && saved.PeerType == pending.PeerType))
                    settings.PendingChats.Add(pending);
            }
        }, cancellationToken);
    }

    public Task UpdatePendingChatPeriodAsync(long owner, long peerId, string peerType, int days,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(days));
        return UpdateAsync(settings =>
        {
            var chat = settings.PendingChats.Single(chat => chat.TelegramUserId == owner &&
                chat.PeerId == peerId && chat.PeerType == peerType);
            chat.HistoryDays = days;
        }, cancellationToken);
    }

    public Task RemovePendingChatAsync(long owner, long peerId, string peerType,
        CancellationToken cancellationToken = default)
        => UpdateAsync(settings => settings.PendingChats.RemoveAll(chat => chat.TelegramUserId == owner &&
            chat.PeerId == peerId && chat.PeerType == peerType), cancellationToken);

    public Task SaveChatAsync(ConfiguredChat chat, CancellationToken cancellationToken = default)
    {
        if (chat.TelegramUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(chat), "Чат должен принадлежать Telegram-аккаунту.");
        return UpdateAsync(settings =>
        {
            settings.Chats ??= [];
            var index = settings.Chats.FindIndex(existing =>
                existing.TelegramUserId == chat.TelegramUserId &&
                existing.PeerId == chat.PeerId && existing.PeerType == chat.PeerType);
            if (index < 0)
                settings.Chats.Add(chat);
            else
            {
                var previous = settings.Chats[index];
                if (string.IsNullOrEmpty(chat.ParticipantRuleset))
                    chat.ParticipantRuleset = previous.ParticipantRuleset;
                chat.HistoryDays = previous.HistoryDays;
                chat.SynchronizationState = previous.SynchronizationState;
                chat.ImportedMessageCount = previous.ImportedMessageCount;
                chat.SynchronizationError = previous.SynchronizationError;
                chat.LastProcessedMessageId = previous.LastProcessedMessageId;
                chat.RequestedHistoryDays = previous.RequestedHistoryDays;
                settings.Chats[index] = chat;
            }
        }, cancellationToken);
    }

    public Task UpdateChatPeriodAsync(long owner, long peerId, string peerType, int days,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(days));
        return UpdateAsync(settings =>
        {
            var chat = FindChat(settings, owner, peerId, peerType);
            if (chat.SynchronizationState == ChatSynchronizationState.COMPLETED)
                chat.RequestedHistoryDays = days == chat.HistoryDays ? null : days;
            else
                chat.HistoryDays = days;
        }, cancellationToken);
    }

    public Task ConfirmChatHistoryReloadAsync(long owner, long peerId, string peerType, int days,
        int messageCount, long? lastMessageId, CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(days));
        if (messageCount < 0) throw new ArgumentOutOfRangeException(nameof(messageCount));
        return UpdateAsync(settings =>
        {
            var chat = FindChat(settings, owner, peerId, peerType);
            if (chat.SynchronizationState != ChatSynchronizationState.COMPLETED)
                throw new InvalidOperationException("Повторная загрузка доступна только завершённому чату.");
            chat.HistoryDays = days;
            chat.RequestedHistoryDays = null;
            chat.ImportedMessageCount = messageCount;
            chat.LastProcessedMessageId = lastMessageId;
            chat.SynchronizationError = null;
        }, cancellationToken);
    }

    public Task UpdateChatSyncAsync(long owner, long peerId, string peerType,
        ChatSynchronizationState state, int messageCount, long? lastMessageId, string? error,
        CancellationToken cancellationToken = default)
    {
        if (messageCount < 0) throw new ArgumentOutOfRangeException(nameof(messageCount));
        return UpdateAsync(settings =>
        {
            var chat = FindChat(settings, owner, peerId, peerType);
            chat.SynchronizationState = state;
            chat.ImportedMessageCount = messageCount;
            chat.LastProcessedMessageId = lastMessageId;
            chat.SynchronizationError = error;
        }, cancellationToken);
    }

    private static ConfiguredChat FindChat(AppSettings settings, long owner, long peerId, string peerType)
    {
        if (owner <= 0) throw new ArgumentOutOfRangeException(nameof(owner));
        return settings.Chats.Single(chat => chat.TelegramUserId == owner &&
            chat.PeerId == peerId && chat.PeerType == peerType);
    }

    private async Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            var settings = await LoadInternalAsync(cancellationToken);
            update(settings);
            await SaveInternalAsync(settings, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<AppSettings> LoadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        await using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, options: FileOptions.Asynchronous);

        return await JsonSerializer.DeserializeAsync<AppSettings>(
            stream, _jsonOptions, cancellationToken) ?? new AppSettings();
    }

    private async Task SaveInternalAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(
                temporaryPath, json, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException) { /* временный файл можно удалить позднее */ }
            catch (UnauthorizedAccessException) { /* сохранённый settings.json не затронут */ }
        }
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(JsonSettingsService));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            await _semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(JsonSettingsService));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            _semaphore.Release();
            throw new ObjectDisposedException(nameof(JsonSettingsService));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _shutdown.Cancel();

        // Existing file operations can still complete their finally/Release.
        // Dispose must never destroy the semaphore while it is in use.
    }
}
