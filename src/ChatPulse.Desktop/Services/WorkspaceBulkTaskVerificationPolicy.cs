namespace ChatPulse.Desktop.Services;

/// <summary>
/// Bulk checks use the same automatic suggestions as the individual verification dialog.
/// Only already-completed tasks are eligible; a post like cannot be deselected.
/// </summary>
public enum WorkspaceBulkVerificationMode
{
    ExcludePreviouslyChecked,
    CheckAll
}

public static class WorkspaceBulkTaskVerificationPolicy
{
    public static (bool Like, bool Comment, bool Save, bool Repost) PostChecks(string? text)
    {
        var suggested = InstagramTaskVerificationRules.SuggestChecks(text);
        return (true, suggested.Comment, suggested.Save, suggested.Repost);
    }

    // A prior unconfirmed action requires manual review, regardless of whether
    // this is a post, subscription or unsubscription. Technical failures retry.
    public static bool ShouldExcludeFromBulk(bool hasManualReviewReport,
        WorkspaceBulkVerificationMode mode = WorkspaceBulkVerificationMode.ExcludePreviouslyChecked) =>
        mode == WorkspaceBulkVerificationMode.ExcludePreviouslyChecked && hasManualReviewReport;

    public static bool ShouldContinue(bool stopRequested) => !stopRequested;
}
