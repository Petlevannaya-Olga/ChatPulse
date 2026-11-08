using System.IO;
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
        => UpdateAsync(settings => settings.IsOnboardingCompleted = true, cancellationToken);

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
                settings.Chats[index] = chat;
        }, cancellationToken);
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
