using OpenQA.Selenium;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// A failure of the controlled Chrome instance or the authenticated Instagram session.
/// Unlike an inconclusive post/profile check, this must stop the entire bulk run.
/// </summary>
public sealed class InstagramBrowserSessionException : Exception
{
    public InstagramBrowserSessionException(string message) : base(message) { }

    public InstagramBrowserSessionException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// A technical fault interrupted the current bulk task. The remaining tasks must not run.
/// Unlike an unconfirmed Instagram action, this is not a failed verification result.
/// </summary>
public sealed class WorkspaceBulkTechnicalFailureException : Exception
{
    public WorkspaceBulkTechnicalFailureException(string message) : base(message) { }

    public WorkspaceBulkTechnicalFailureException(string message, Exception innerException)
        : base(message, innerException) { }
}

public static class InstagramBrowserFailure
{
    /// <summary>
    /// A stale/re-rendered DOM node or a missing/click-blocked element is a problem
    /// with the current page, not evidence that Chrome or the session is lost.
    /// Other Selenium driver failures are fatal during batch verification.
    /// </summary>
    public static bool IsFatal(WebDriverException exception) => exception is not
        (StaleElementReferenceException or NoSuchElementException or
            ElementClickInterceptedException or ElementNotInteractableException);

    public static void ThrowIfFatal(WebDriverException exception, string context)
    {
        if (IsFatal(exception))
            throw new InstagramBrowserSessionException($"{context}: {exception.Message}", exception);
    }

    /// <summary>
    /// Some Instagram checks return diagnostic text instead of throwing.
    /// In a bulk run, those technical failures must also abort the queue.
    /// A genuinely unconfirmed action is not a technical failure.
    /// </summary>
    public static void ThrowIfTechnicalResult(InstagramTaskCheckResult? result)
    {
        if (result is { Status: InstagramTaskCheckStatus.INCONCLUSIVE } &&
            WorkspaceFailedTaskReport.IsTechnicalDiagnostic(result.Detail))
            throw new WorkspaceBulkTechnicalFailureException(result.Detail);
    }
}
