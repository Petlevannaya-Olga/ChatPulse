using System.Text.RegularExpressions;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Uses the historical 14-chat format for its original chat and for chats that
/// were explicitly connected with the same ruleset. No rules are inferred from
/// unrelated chat names.
/// </summary>
public static class ChatParticipantHistoryRuleSelection
{
    public const string CHAT_14_RULESET = "Chat14Rules";

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
        return string.Equals(chat.ParticipantRuleset, CHAT_14_RULESET,
                   StringComparison.Ordinal) ||
               CHAT_14_NUMBER.IsMatch(chat.TelegramTitle) ||
               CHAT_14_NUMBER.IsMatch(chat.DisplayName);
    }
}
