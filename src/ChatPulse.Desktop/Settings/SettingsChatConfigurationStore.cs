using ChatPulse.Application.Services;

namespace ChatPulse.Desktop.Settings;

/// <summary>Настройки чатов хранятся в общем settings.json, как и остальные настройки онбординга.</summary>
public sealed class SettingsChatConfigurationStore(ISettingsService settings) : IChatConfigurationStore
{
    public Task SaveAsync(
        long telegramUserId,
        long peerId,
        string peerType,
        string telegramTitle,
        string displayName,
        string socialNetwork,
        DateOnly startDate,
        CancellationToken cancellationToken)
        => settings.SaveChatAsync(new ConfiguredChat
        {
            TelegramUserId = telegramUserId,
            PeerId = peerId,
            PeerType = peerType,
            TelegramTitle = telegramTitle,
            DisplayName = displayName,
            SocialNetwork = socialNetwork,
            StartDate = startDate,
        }, cancellationToken);
}
