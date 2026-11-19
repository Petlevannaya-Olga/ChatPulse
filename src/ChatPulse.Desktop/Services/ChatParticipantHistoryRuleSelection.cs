using System.Text.RegularExpressions;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Explicitly limits the first historical-import ruleset to chat 14.
/// Other chats require their own rules before importing their history.
/// </summary>
public static class ChatParticipantHistoryRuleSelection
{
    private static readonly TimeSpan REGEX_TIMEOUT = TimeSpan.FromMilliseconds(100);
    private static readonly Regex CHAT_14_NUMBER = new(
        @"(?<!\d)14(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        REGEX_TIMEOUT);

    public static bool SupportsChat14(ConfiguredChat? chat)
    {
        if (chat is null || chat.SynchronizationState != ChatSynchronizationState.COMPLETED ||
            !string.Equals(chat.SocialNetwork, "Instagram", StringComparison.OrdinalIgnoreCase) ||
            chat.TelegramUserId <= 0 || chat.PeerId == 0 ||
            chat.PeerType is not ("Chat" or "Channel"))
            return false;

        // Chat numbers in names are a provisional, conservative routing rule.
        // Do not silently apply the chat-14 parser to unrelated Telegram chats.
        return CHAT_14_NUMBER.IsMatch(chat.TelegramTitle) ||
               CHAT_14_NUMBER.IsMatch(chat.DisplayName);
    }
}
