using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>Scopes manual synchronization to only the intended connected chats.</summary>
public static class WorkspaceSynchronizationSelection
{
    public static IReadOnlyList<ConfiguredChat> Select(
        IEnumerable<ConfiguredChat> configured, ConfiguredChat? onlyChat = null)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return configured.Where(chat =>
            chat.SynchronizationState == ChatSynchronizationState.COMPLETED &&
            (onlyChat is null ||
             chat.TelegramUserId == onlyChat.TelegramUserId &&
             chat.PeerType == onlyChat.PeerType &&
             chat.PeerId == onlyChat.PeerId)).ToArray();
    }
}
