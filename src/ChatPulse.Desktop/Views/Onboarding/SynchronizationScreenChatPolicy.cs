using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Views.Onboarding;

/// <summary>
/// Builds the step-four view from a saved snapshot for the current Telegram owner.
/// Connected chats take precedence over duplicate pending candidates.
/// </summary>
public static class SynchronizationScreenChatPolicy
{
    public static SynchronizationChatRow[] CreateRows(AppSettings settings, long owner)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (owner <= 0) return [];

        var seen = new HashSet<(long PeerId, string PeerType)>();
        var result = new List<SynchronizationChatRow>();
        foreach (var chat in settings.Chats)
        {
            if (chat.TelegramUserId == owner && seen.Add((chat.PeerId, chat.PeerType)))
                result.Add(new SynchronizationChatRow(chat));
        }
        foreach (var pending in settings.PendingChats ?? [])
        {
            if (pending.TelegramUserId == owner && seen.Add((pending.PeerId, pending.PeerType)))
                result.Add(new SynchronizationChatRow(pending));
        }
        return [.. result];
    }
}
