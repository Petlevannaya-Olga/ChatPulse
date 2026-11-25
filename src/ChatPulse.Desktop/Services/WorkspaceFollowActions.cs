using System.Text.RegularExpressions;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>One Instagram action within a single Telegram message.</summary>
public sealed record WorkspaceFollowAction(WorkspaceTaskKind Kind, string TargetUserName)
{
    public bool ShouldFollow => Kind == WorkspaceTaskKind.SUBSCRIBE;
}

/// <summary>
/// Recognizes administrator announcements with separately headed join/leave sections.
/// Legacy subscription tasks still use the first-profile rule; these announcements
/// may contain multiple subscriptions AND unsubscriptions in Telegram message order.
/// </summary>
public static class WorkspaceFollowActions
{
    // Administrator announcements vary in wording. Recognize an explicit request
    // to follow and explicit removal from the chat as separate action headings.
    // "Подписка по желанию" is not a heading: it is a note about the preceding action.
    private const string SubscribeHeading =
        @"(?:\bвзаимн\p{L}*\s+подписк\p{L}*|\bпрошу\s+подписат\p{L}*\b)";

    private const string LeaveHeading =
        @"(?:\bнаш\s+чат\s+покинули\b|\b(?:удалена|удален|удалён|удалены|удалили|исключена|исключен|исключён|исключены|исключили)\s+из\s+чата\b)";

    private static readonly Regex AnnouncementHeader = new(
        @"(?m)^[^\r\n]*(?:" + SubscribeHeading + "|" + LeaveHeading + @")[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Regex LeaveHeader = new(
        LeaveHeading,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Regex ProfileLinks = new(
        @"https?://[^\s<>\[\]""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Returns false for ordinary tasks, leaving their historical rules untouched.</summary>
    public static bool TryParseAnnouncement(string? text, out IReadOnlyList<WorkspaceFollowAction> actions)
    {
        actions = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var headings = AnnouncementHeader.Matches(text);
        if (headings.Count == 0) return false;

        var parsed = new List<WorkspaceFollowAction>();
        var distinct = new HashSet<(WorkspaceTaskKind Kind, string Target)>();
        for (var i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            var kind = LeaveHeader.IsMatch(heading.Value)
                ? WorkspaceTaskKind.UNSUBSCRIBE : WorkspaceTaskKind.SUBSCRIBE;
            var blockEnd = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            var block = text.Substring(heading.Index, blockEnd - heading.Index);
            foreach (Match link in ProfileLinks.Matches(block))
            {
                // Telegram's Markdown rendering repeats the URL as [label](destination).
                // A target must be checked just once per action kind and message.
                var url = link.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '}', '»');
                var target = InstagramActivityProfile.TryGetUserName(url);
                if (target is null) continue;
                var normalized = InstagramUserName.Normalize(target);
                if (distinct.Add((kind, normalized)))
                    parsed.Add(new WorkspaceFollowAction(kind, normalized));
            }
        }
        if (parsed.Count == 0) return false;
        actions = parsed;
        return true;
    }

    public static IReadOnlyList<WorkspaceFollowAction> GetActions(string? text, string? postUrl)
    {
        // Posts/Reels always take precedence, even with a profile and follow words.
        var kind = WorkspaceTaskKindDetector.Detect(text, postUrl);
        if (kind == WorkspaceTaskKind.TASK) return [];
        if (TryParseAnnouncement(text, out var announcement)) return announcement;
        return InstagramFollowTaskRules.GetVerificationTargets(kind, text, postUrl)
            .Select(name => new WorkspaceFollowAction(kind, name)).ToArray();
    }

    public static IReadOnlyList<WorkspaceFollowAction> WithoutOwnAccounts(
        IReadOnlyList<WorkspaceFollowAction> actions, InstagramOwnAccounts? accounts, string viewer)
    {
        var own = InstagramOwnAccountFilter.GetUserNames(accounts, viewer);
        return actions.Where(action => !own.Contains(action.TargetUserName)).ToArray();
    }
}
