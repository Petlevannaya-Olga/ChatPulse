using System.Text.RegularExpressions;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Selects the configured participant-list format independently of the social
/// network used for post tasks. A chat-specific format takes precedence over
/// older generic Instagram-chat defaults.
/// </summary>
public static class ChatParticipantHistoryRuleSelection
{
    public const string CHAT_14_RULESET = "Chat14Rules";
    public const string CHAT_16_TELEGRAM_RULESET = "Chat16TelegramRules";

    private static readonly TimeSpan REGEX_TIMEOUT = TimeSpan.FromMilliseconds(100);

    // This Telegram participant-list format can be used in a chat connected as
    // Instagram (the post network and the participant profile network differ).
    // Match both observed variants of the distinctive title: "16 Пост чат тк"
    // and "16 Пост чат тгк". The latter is the actual Telegram chat title.
    // Never apply these rules merely because another chat includes number 16.
    private static readonly Regex CHAT_16_TELEGRAM_TITLE = new(
        @"(?<!\d)16[\s\p{P}\p{S}]*пост[\s\p{P}\p{S}]*чат[\s\p{P}\p{S}]*(?:тгк|тк)(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        REGEX_TIMEOUT);

    public static bool SupportsChat16Telegram(ConfiguredChat? chat)
    {
        if (!IsConnectedChat(chat) ||
            (!string.Equals(chat!.SocialNetwork, "Instagram", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(chat.SocialNetwork, "Telegram", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Older Instagram chat connections received Chat14Rules automatically.
        // The precise 16-post-chat title takes precedence over that legacy tag.
        return string.Equals(chat.ParticipantRuleset, CHAT_16_TELEGRAM_RULESET,
                   StringComparison.Ordinal) ||
               CHAT_16_TELEGRAM_TITLE.IsMatch(chat.TelegramTitle ?? string.Empty) ||
               CHAT_16_TELEGRAM_TITLE.IsMatch(chat.DisplayName ?? string.Empty);
    }

    private static bool IsConnectedChat(ConfiguredChat? chat) =>
        chat is not null &&
        chat.SynchronizationState == ChatSynchronizationState.COMPLETED &&
        chat.TelegramUserId > 0 && chat.PeerId != 0 &&
        chat.PeerType is "Chat" or "Channel";

    public static IChatParticipantHistoryParser? CreateConfiguredParser(ConfiguredChat? chat)
    {
        // The 16-chat format must win over Chat14Rules assigned by older
        // Instagram-chat onboarding; otherwise the wrong parser is selected.
        if (SupportsChat16Telegram(chat)) return new Chat16TelegramParticipantHistoryParser();
        if (SupportsChat14(chat)) return new Chat14ParticipantHistoryParser();
        return null;
    }

    private static readonly Regex CHAT_14_NUMBER = new(
        @"(?<!\d)14(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        REGEX_TIMEOUT);

    public static bool SupportsChat14(ConfiguredChat? chat)
    {
        if (!IsConnectedChat(chat) ||
            !string.Equals(chat!.SocialNetwork, "Instagram", StringComparison.OrdinalIgnoreCase) ||
            SupportsChat16Telegram(chat))
            return false;

        // Chat numbers in names are a provisional, conservative routing rule.
        // Do not silently apply the chat-14 parser to unrelated Telegram chats.
        return string.Equals(chat.ParticipantRuleset, CHAT_14_RULESET,
                   StringComparison.Ordinal) ||
               CHAT_14_NUMBER.IsMatch(chat.TelegramTitle) ||
               CHAT_14_NUMBER.IsMatch(chat.DisplayName);
    }
}
