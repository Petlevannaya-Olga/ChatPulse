namespace ChatPulse.Desktop.Services;

/// <summary>
/// Bulk checks use the same automatic suggestions as the individual verification dialog.
/// Only already-completed tasks are eligible; a post like cannot be deselected.
/// </summary>
public static class WorkspaceBulkTaskVerificationPolicy
{
    public static (bool Like, bool Comment, bool Save, bool Repost) PostChecks(string? text)
    {
        var suggested = InstagramTaskVerificationRules.SuggestChecks(text);
        return (true, suggested.Comment, suggested.Save, suggested.Repost);
    }

    // A post/Reel with an unsuccessful report is handled by manual rechecking only.
    // Subscription and unsubscription tasks keep their normal bulk behavior.
    public static bool ShouldExcludeFailedPost(WorkspaceTaskKind kind, bool hasFailedReport) =>
        hasFailedReport && kind is not (WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE);

    public static bool ShouldContinue(bool stopRequested) => !stopRequested;
}
