using System.Text.RegularExpressions;

namespace ChatPulse.Contracts.Telegram;

/// <summary>Only Telegram chats whose title contains the standalone word «пост» are eligible.</summary>
public static partial class ChatTitlePolicy
{
    public static bool IsEligible(string? title)
        => !string.IsNullOrWhiteSpace(title) && PostWordRegex().IsMatch(title);

    [GeneratedRegex(@"(?<![\p{L}\p{N}\p{M}_])пост(?![\p{L}\p{N}\p{M}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PostWordRegex();
}
