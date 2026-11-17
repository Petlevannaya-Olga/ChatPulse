namespace ChatPulse.Desktop.Settings;

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveOnboardingStepAsync(
        OnboardingStep step, CancellationToken cancellationToken = default);

    Task SaveUserNameAndStepAsync(
        string userName, OnboardingStep step,
        CancellationToken cancellationToken = default);

    Task SaveTelegramPageAsync(
        TelegramOnboardingPage page, CancellationToken cancellationToken = default);

    Task CompleteOnboardingAsync(CancellationToken cancellationToken = default);

    Task SaveChatAsync(ConfiguredChat chat, CancellationToken cancellationToken = default);

    Task SavePendingChatsAsync(long owner, IReadOnlyList<PendingChatConnection> chats,
        CancellationToken cancellationToken = default);

    Task UpdatePendingChatPeriodAsync(long owner, long peerId, string peerType, int days,
        CancellationToken cancellationToken = default);

    Task RemovePendingChatAsync(long owner, long peerId, string peerType,
        CancellationToken cancellationToken = default);

    Task UpdateChatPeriodAsync(long owner, long peerId, string peerType, int days,
        CancellationToken cancellationToken = default);

    Task ConfirmChatHistoryReloadAsync(long owner, long peerId, string peerType, int days,
        int messageCount, long? lastMessageId, CancellationToken cancellationToken = default);

    Task UpdateChatSyncAsync(long owner, long peerId, string peerType,
        ChatSynchronizationState state, int messageCount, long? lastMessageId, string? error,
        CancellationToken cancellationToken = default);
}
