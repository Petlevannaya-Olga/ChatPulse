namespace ChatPulse.Desktop.Views.Onboarding;

/// <summary>
/// Automatic reconnect retries only connection failures. Explicit user retry
/// also retries non-network analysis errors without repeating successful chats.
/// </summary>
public static class ChatReviewRetryPolicy
{
    public static bool ShouldRetry(ChatAnalysisResultRow row, bool manuallyRequested)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.NeedsConnectionRetry || (manuallyRequested && row.StatusKind == "Error");
    }
}
