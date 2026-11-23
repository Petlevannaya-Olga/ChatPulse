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
    /// A subscription targets the FIRST profile: additional accounts can be used to give activity.
    /// For a mass unsubscription, EVERY distinct profile in message order must be verified.
    /// </summary>
    public static IReadOnlyList<string> GetVerificationTargets(WorkspaceTaskKind kind,
        string? taskText, string? postUrl)
    {
        var profiles = WorkspaceTaskLinks.Split(taskText)
            .Where(part => part.Url is not null)
            .Select(part => InstagramActivityProfile.TryGetUserName(part.Url))
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (profiles.Length > 0)
            return kind == WorkspaceTaskKind.UNSUBSCRIBE ? profiles : [profiles[0]];

        // Older tasks may store the only profile URL in PostUrl.
        var fallback = InstagramActivityProfile.TryGetUserName(postUrl);
        return fallback is null ? [] : [fallback];
    }

    /// <summary>Keep the historical first-profile rule for subscription detection.</summary>
    public static IReadOnlyList<string> GetProfileTargets(string? taskText, string? postUrl) =>
        GetVerificationTargets(WorkspaceTaskKind.SUBSCRIBE, taskText, postUrl);

    /// <summary>
    /// Checks all targets sequentially, including after an inconclusive result.
    /// Each target has its own result so the failed report can identify every discrepancy.
    /// </summary>
    public static async Task<InstagramFollowTaskVerification> VerifyTargetsAsync(
        string viewerUserName, WorkspaceTaskKind kind, IReadOnlyList<string> targets,
        Func<string, bool, CancellationToken, Task<InstagramTaskCheckResult>> check,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (kind is not (WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(check);
        if (targets.Count == 0 || targets.Any(string.IsNullOrWhiteSpace) ||
            targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new ArgumentException("Нужен непустой список различных Instagram-профилей.", nameof(targets));

        var checks = new List<InstagramFollowTargetCheck>(targets.Count);
        for (var index = 0; index < targets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = targets[index];
            progress?.Report($"{(kind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка")}: " +
                $"{index + 1}/{targets.Count} — @{name}");
            InstagramTaskCheckResult result;
            try
            {
                result = await check(name, kind == WorkspaceTaskKind.SUBSCRIBE, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One failed page must not silently skip all remaining participants.
                result = InstagramTaskCheckResult.Inconclusive($"@{name}: техническая ошибка: {exception.Message}");
            }
            checks.Add(new InstagramFollowTargetCheck(name, result));
        }

        var confirmed = checks.Count(item => item.Check.Status == InstagramTaskCheckStatus.CONFIRMED);
        var aggregate = confirmed == targets.Count
            ? InstagramTaskCheckResult.Confirmed($"Подтверждено {confirmed} из {targets.Count}.")
            : InstagramTaskCheckResult.Inconclusive($"Подтверждено {confirmed} из {targets.Count}. " +
                "Не все действия выполнены или проверены.");
        return new InstagramFollowTaskVerification(viewerUserName, targets[0], kind, aggregate, checks);
    }
}

public sealed record InstagramFollowTargetCheck(string TargetUserName, InstagramTaskCheckResult Check);

public sealed record InstagramFollowTaskVerification(
    string ViewerUserName, string TargetUserName, WorkspaceTaskKind TaskKind,
    InstagramTaskCheckResult Check, IReadOnlyList<InstagramFollowTargetCheck>? TargetChecks = null)
{
    public IReadOnlyList<InstagramFollowTargetCheck> Results =>
        TargetChecks ?? [new InstagramFollowTargetCheck(TargetUserName, Check)];

    public bool CanHide =>
        (TaskKind is WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE) &&
        Check.Status == InstagramTaskCheckStatus.CONFIRMED &&
        Results.Count > 0 && Results.All(result => result.Check.Status == InstagramTaskCheckStatus.CONFIRMED);

    public string TargetProfileUrl => $"https://www.instagram.com/{InstagramUserName.Normalize(TargetUserName)}/";
}
