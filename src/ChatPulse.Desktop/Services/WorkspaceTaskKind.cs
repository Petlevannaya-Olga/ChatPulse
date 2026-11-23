using System.Text;
using System.Text.RegularExpressions;

namespace ChatPulse.Desktop.Services;

public enum WorkspaceTaskKind { TASK, SUBSCRIBE, UNSUBSCRIBE }

/// <summary>Display classification only; does not change Telegram content or task detection.</summary>
public static class WorkspaceTaskKindDetector
{
    // Formatting and emoji may appear between letters, including custom emoji placeholders.
    private const string JOIN = @"[\p{M}\p{S}\p{P}\p{Cf}\p{Cs}\s]{0,8}";
    private static readonly Regex UNSUBSCRIBE = CreatePattern("отписка");
    private static readonly Regex SUBSCRIBE = CreatePattern("подписка");

    public static WorkspaceTaskKind Detect(string? message, string? postUrl = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return WorkspaceTaskKind.TASK;
        var normalized = message.Normalize(NormalizationForm.FormKC);

        // Words such as «Подписка» may occur in ordinary engagement tasks (like,
        // comment, save, repost). Only an actual Instagram profile link can make
        // a message a follow/unfollow task. /p/ and /reel/ links are not profiles.
        if (!UNSUBSCRIBE.IsMatch(normalized) && !SUBSCRIBE.IsMatch(normalized))
            return WorkspaceTaskKind.TASK;
        if (InstagramFollowTaskRules.GetProfileTargets(message, postUrl).Count == 0)
            return WorkspaceTaskKind.TASK;

        if (UNSUBSCRIBE.IsMatch(normalized)) return WorkspaceTaskKind.UNSUBSCRIBE;
        return WorkspaceTaskKind.SUBSCRIBE;
    }

    private static Regex CreatePattern(string word)
        => new(@"(?<!\p{L})" + string.Join(JOIN, word.Select(character => Regex.Escape(character.ToString()))) +
            @"(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(150));
}
