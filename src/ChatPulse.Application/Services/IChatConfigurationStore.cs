namespace ChatPulse.Application.Services;

/// <summary>Абстракция сохранения пользовательской конфигурации чатов.</summary>
public interface IChatConfigurationStore
{
    Task SaveAsync(
        long peerId,
        string peerType,
        string telegramTitle,
        string displayName,
        string socialNetwork,
        DateOnly startDate,
        CancellationToken cancellationToken);
}
