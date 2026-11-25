using ChatPulse.Desktop.Settings;
using OpenQA.Selenium;

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
        // Administrator join/leave announcements can contain multiple instructions.
        // Do not apply the legacy first-profile rule to these sections.
        if (WorkspaceFollowActions.TryParseAnnouncement(taskText, out var announcements))
            return announcements.Where(action => action.Kind == kind)
                .Select(action => action.TargetUserName).ToArray();

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
    public static Task<InstagramFollowTaskVerification> VerifyTargetsAsync(
        string viewerUserName, WorkspaceTaskKind kind, IReadOnlyList<string> targets,
        Func<string, bool, CancellationToken, Task<InstagramTaskCheckResult>> check,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        bool stopOnTechnicalFailure = false)
    {
        if (kind is not (WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0 || targets.Any(string.IsNullOrWhiteSpace) ||
            targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new ArgumentException("Нужен непустой список различных Instagram-профилей.", nameof(targets));
        return VerifyActionsAsync(viewerUserName,
            targets.Select(name => new WorkspaceFollowAction(kind, name)).ToArray(),
            check, progress, cancellationToken, stopOnTechnicalFailure);
    }

    /// <summary>Verify every join/leave action in the original Telegram message order.</summary>
    public static async Task<InstagramFollowTaskVerification> VerifyActionsAsync(
        string viewerUserName, IReadOnlyList<WorkspaceFollowAction> actions,
        Func<string, bool, CancellationToken, Task<InstagramTaskCheckResult>> check,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        bool stopOnTechnicalFailure = false)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(check);
        if (actions.Count == 0 || actions.Any(action =>
                action.Kind is not (WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE) ||
                string.IsNullOrWhiteSpace(action.TargetUserName)) ||
            actions.Select(action => (action.Kind, action.TargetUserName.ToLowerInvariant()))
                .Distinct().Count() != actions.Count)
            throw new ArgumentException("Нужен непустой список уникальных действий.", nameof(actions));
        // Asking to subscribe and unsubscribe from the same account at once cannot
        // have a consistent, confirmable Instagram state.
        if (actions.Select(action => action.TargetUserName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != actions.Count)
            throw new ArgumentException("Для одного профиля найдены противоречащие действия.", nameof(actions));

        var checks = new List<InstagramFollowTargetCheck>(actions.Count);
        for (var index = 0; index < actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = actions[index];
            var name = action.TargetUserName;
            progress?.Report($"{(action.ShouldFollow ? "Подписка" : "Отписка")}: " +
                $"{index + 1}/{actions.Count} — @{name}");
            InstagramTaskCheckResult result;
            try
            {
                result = await check(name, action.ShouldFollow, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InstagramBrowserSessionException)
            {
                throw;
            }
            catch (WebDriverException exception) when (InstagramBrowserFailure.IsFatal(exception))
            {
                throw new InstagramBrowserSessionException(
                    $"Ошибка Chrome при проверке @{name}: {exception.Message}", exception);
            }
            catch (Exception exception)
            {
                if (stopOnTechnicalFailure)
                    throw new WorkspaceBulkTechnicalFailureException(
                        $"Техническая ошибка при проверке @{name}: {exception.Message}", exception);
                result = InstagramTaskCheckResult.Inconclusive($"@{name}: техническая ошибка: {exception.Message}");
            }
            if (stopOnTechnicalFailure)
                InstagramBrowserFailure.ThrowIfTechnicalResult(result);
            checks.Add(new InstagramFollowTargetCheck(name, result, action.Kind));
        }

        var confirmed = checks.Count(item => item.Check.Status == InstagramTaskCheckStatus.CONFIRMED);
        var aggregate = confirmed == actions.Count
            ? InstagramTaskCheckResult.Confirmed($"Подтверждено {confirmed} из {actions.Count}.")
            : InstagramTaskCheckResult.Inconclusive($"Подтверждено {confirmed} из {actions.Count}. " +
                "Не все действия выполнены или проверены.");
        var kind = actions.All(action => action.Kind == actions[0].Kind)
            ? actions[0].Kind : WorkspaceTaskKind.MIXED;
        return new InstagramFollowTaskVerification(viewerUserName, actions[0].TargetUserName,
            kind, aggregate, checks);
    }

}

public sealed record InstagramFollowTargetCheck(string TargetUserName, InstagramTaskCheckResult Check,
    WorkspaceTaskKind? ActionKind = null);

public sealed record InstagramFollowTaskVerification(
    string ViewerUserName, string TargetUserName, WorkspaceTaskKind TaskKind,
    InstagramTaskCheckResult Check, IReadOnlyList<InstagramFollowTargetCheck>? TargetChecks = null)
{
    public IReadOnlyList<InstagramFollowTargetCheck> Results =>
        TargetChecks ?? [new InstagramFollowTargetCheck(TargetUserName, Check)];

    public bool CanHide =>
        (TaskKind is WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE or WorkspaceTaskKind.MIXED) &&
        Check.Status == InstagramTaskCheckStatus.CONFIRMED &&
        Results.Count > 0 && Results.All(result => result.Check.Status == InstagramTaskCheckStatus.CONFIRMED);

    public string TargetProfileUrl => $"https://www.instagram.com/{InstagramUserName.Normalize(TargetUserName)}/";
}
