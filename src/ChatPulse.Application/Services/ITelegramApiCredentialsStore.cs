namespace ChatPulse.Application.Services;

/// <summary>Секреты конкретного пользователя Windows. Не хранить в settings.json.</summary>
public sealed record TelegramApiCredentials(int ApiId, string ApiHash);

public interface ITelegramApiCredentialsStore
{
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);
    Task<TelegramApiCredentials?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TelegramApiCredentials credentials, CancellationToken cancellationToken = default);
}
