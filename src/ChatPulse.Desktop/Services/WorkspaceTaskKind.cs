using System.Text;
using System.Text.RegularExpressions;

namespace ChatPulse.Desktop.Services;

public enum WorkspaceTaskKind { TASK, SUBSCRIBE, UNSUBSCRIBE, MIXED, STORY }

/// <summary>Classifies Telegram tasks for display and verification routing.</summary>
public static class WorkspaceTaskKindDetector
{
    // Formatting and emoji may appear between letters, including custom emoji placeholders.
    private const string JOIN = @"[\p{M}\p{S}\p{P}\p{Cf}\p{Cs}\s]{0,8}";
    private static readonly Regex UNSUBSCRIBE = CreatePattern("отписка");
    private static readonly Regex SUBSCRIBE = CreatePattern("подписка");

    public static WorkspaceTaskKind Detect(string? message, string? postUrl = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return WorkspaceStoryTaskRules.IsStoryOnlyTask(message, postUrl)
                ? WorkspaceTaskKind.STORY : WorkspaceTaskKind.TASK;
        var normalized = message.Normalize(NormalizationForm.FormKC);

        // Post/Reel links take priority over follow/unfollow instructions, even if
        // the same message explicitly asks to subscribe and contains a profile URL.
        // The task is verified as engagement with the publication, not as a follow.
        if (HasPublicationLink(message, postUrl)) return WorkspaceTaskKind.TASK;

        // Only direct Stories links (and optional profile links): check likes only.
        if (WorkspaceStoryTaskRules.IsStoryOnlyTask(message, postUrl))
            return WorkspaceTaskKind.STORY;

        if (WorkspaceFollowActions.TryParseAnnouncement(message, out var actions))
        {
            var first = actions[0].Kind;
            return actions.Any(action => action.Kind != first) ? WorkspaceTaskKind.MIXED : first;
        }

        // Words such as «Подписка» may occur in ordinary engagement tasks (like,
        // comment, save, repost). Only an actual Instagram profile link can make
        // a message a follow/unfollow task.
        if (!UNSUBSCRIBE.IsMatch(normalized) && !SUBSCRIBE.IsMatch(normalized))
            return WorkspaceTaskKind.TASK;
        if (InstagramFollowTaskRules.GetProfileTargets(message, postUrl).Count == 0)
            return WorkspaceTaskKind.TASK;

        if (UNSUBSCRIBE.IsMatch(normalized)) return WorkspaceTaskKind.UNSUBSCRIBE;
        return WorkspaceTaskKind.SUBSCRIBE;
    }

    private static bool HasPublicationLink(string message, string? postUrl) =>
        IsPublicationUrl(postUrl) || WorkspaceTaskLinks.Split(message)
            .Any(link => IsPublicationUrl(link.Url));

    private static bool IsPublicationUrl(string? candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            (!string.Equals(uri.Host, "instagram.com", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Host, "www.instagram.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
            (string.Equals(segments[0], "p", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(segments[0], "reel", StringComparison.OrdinalIgnoreCase));
    }

    private static Regex CreatePattern(string word)
        => new(@"(?<!\p{L})" + string.Join(JOIN, word.Select(character => Regex.Escape(character.ToString()))) +
            @"(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(150));
}
