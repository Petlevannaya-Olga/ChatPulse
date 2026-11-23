using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>What Instagram's own profile-action button currently offers the signed-in viewer.</summary>
public enum InstagramFollowButtonState { FOLLOWING, NOT_FOLLOWING, UNKNOWN }

public static class InstagramFollowTaskRules
{
    public static InstagramFollowButtonState Classify(IEnumerable<string> buttonLabels)
    {
        ArgumentNullException.ThrowIfNull(buttonLabels);
        var labels = buttonLabels.Select(label => (label ?? string.Empty).Trim().ToLowerInvariant())
            .Where(label => label.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var following = labels.Count(label => label is "подписки" or "вы подписаны" or
            "отписаться" or "following" or "unfollow");
        var notFollowing = labels.Count(label => label is "подписаться" or
            "подписаться в ответ" or "follow" or "follow back");
        // Conflicting controls or an unknown request state cannot prove the result.
        if (following + notFollowing != 1) return InstagramFollowButtonState.UNKNOWN;
        return following == 1 ? InstagramFollowButtonState.FOLLOWING : InstagramFollowButtonState.NOT_FOLLOWING;
    }

    /// <summary>
    /// For subscription tasks, the first Instagram profile in the message is the account
    /// to follow. Other profiles may describe where a participant gives activity and must
    /// never become alternative verification targets.
    /// </summary>
    public static IReadOnlyList<string> GetProfileTargets(string? taskText, string? postUrl)
    {
        var target = WorkspaceTaskLinks.Split(taskText)
            .Where(part => part.Url is not null)
            .Select(part => InstagramActivityProfile.TryGetUserName(part.Url))
            .FirstOrDefault(name => name is not null);
        if (target is not null) return [target];

        // Legacy tasks may store the only profile link in PostUrl.
        var fallback = InstagramActivityProfile.TryGetUserName(postUrl);
        return fallback is null ? [] : [fallback];
    }
}

public sealed record InstagramFollowTaskVerification(
    string ViewerUserName, string TargetUserName, WorkspaceTaskKind TaskKind,
    InstagramTaskCheckResult Check)
{
    public bool CanHide =>
        (TaskKind is WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE) &&
        Check.Status == InstagramTaskCheckStatus.CONFIRMED;

    public string TargetProfileUrl => $"https://www.instagram.com/{InstagramUserName.Normalize(TargetUserName)}/";
}
