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
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatPulse");
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonSettingsService()
    {
        _path = Path.Combine(_directory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
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

    private async Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
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

    public void Dispose() => _semaphore.Dispose();
}
