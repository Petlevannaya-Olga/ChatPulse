using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class OnboardingReviewRetryPolicyTests
{
    [Fact]
    public void AutomaticReconnect_RetriesOnlyConnectionFailures()
    {
        var connectionError = new ChatAnalysisResultRow("Пост чат 1");
        connectionError.SetError("Telegram недоступен", needsConnectionRetry: true);
        var otherError = new ChatAnalysisResultRow("Пост чат 2");
        otherError.SetError("Не удалось разобрать сообщение");

        Assert.True(ChatReviewRetryPolicy.ShouldRetry(connectionError, manuallyRequested: false));
        Assert.False(ChatReviewRetryPolicy.ShouldRetry(otherError, manuallyRequested: false));
    }

    [Fact]
    public void ExplicitRetry_IncludesNonNetworkErrors()
    {
        var otherError = new ChatAnalysisResultRow("Пост чат");
        otherError.SetError("Не удалось проверить чат");

        Assert.True(ChatReviewRetryPolicy.ShouldRetry(otherError, manuallyRequested: true));
    }

    [Fact]
    public void Retry_NeverReanalyzesAlreadySuccessfulChat()
    {
        var row = new ChatAnalysisResultRow("Пост чат");
        row.SetResult(new ChatActivityAnalysis(ChatActivityClassification.POSSIBLE_ACTIVITY,
            ChatTaskNetwork.INSTAGRAM, 1, 0, []), 7, 12);

        Assert.False(ChatReviewRetryPolicy.ShouldRetry(row, manuallyRequested: true));
        Assert.False(ChatReviewRetryPolicy.ShouldRetry(row, manuallyRequested: false));
    }

    [Fact]
    public void RetriedSuccess_IsNoLongerInRetrySet()
    {
        var row = new ChatAnalysisResultRow("Пост чат");
        row.SetError("Telegram недоступен", needsConnectionRetry: true);
        row.SetResult(new ChatActivityAnalysis(ChatActivityClassification.POSSIBLE_ACTIVITY,
            ChatTaskNetwork.TELEGRAM, 1, 0, []), 1, 4);

        Assert.False(ChatReviewRetryPolicy.ShouldRetry(row, manuallyRequested: true));
    }

    [Fact]
    public void NullRow_IsRejected()
        => Assert.Throws<ArgumentNullException>(() =>
            ChatReviewRetryPolicy.ShouldRetry(null!, manuallyRequested: true));
}
