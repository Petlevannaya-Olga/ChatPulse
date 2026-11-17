using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatReviewNavigationLifecycleTests
{
    [Fact]
    public void RapidBackForward_MakesEveryEarlierAnalysisResultStale()
    {
        var generation = new ChatReviewRunGeneration();
        var first = generation.Advance();
        generation.Advance(); // Back
        var second = generation.Advance(); // Forward
        generation.Advance(); // Back again
        var third = generation.Advance(); // Forward again

        Assert.False(generation.IsCurrent(first));
        Assert.False(generation.IsCurrent(second));
        Assert.True(generation.IsCurrent(third));
    }

    [Fact]
    public async Task OldResultCompletesAfterNewResult_ButCannotBeApplied()
    {
        var generation = new ChatReviewRunGeneration();
        var previous = generation.Advance();
        var staleRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeRequest = generation.Advance();
        string? shownStatus = null;

        var updateFromOldRequest = staleRequest.Task.ContinueWith(task =>
        {
            if (generation.IsCurrent(previous)) shownStatus = task.Result;
        }, TaskScheduler.Default);
        if (generation.IsCurrent(activeRequest)) shownStatus = "Новая проверка завершена";
        staleRequest.SetResult("Результат старого запроса");
        await updateFromOldRequest.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Новая проверка завершена", shownStatus);
    }

    [Theory]
    [InlineData(ChatActivityClassification.POSSIBLE_ACTIVITY, ChatTaskNetwork.INSTAGRAM, true)]
    [InlineData(ChatActivityClassification.POSSIBLE_ACTIVITY, ChatTaskNetwork.TELEGRAM, true)]
    [InlineData(ChatActivityClassification.INSUFFICIENT_DATA, null, false)]
    [InlineData(ChatActivityClassification.CONFLICTING_NETWORKS, null, false)]
    public void AnalysisResult_OnlyRecognizedNetworkAllowsProceeding(
        ChatActivityClassification classification, ChatTaskNetwork? network, bool expected)
    {
        var row = new ChatAnalysisResultRow("15 Пост чат");
        row.SetResult(new ChatActivityAnalysis(classification, network, expected ? 1 : 0,
            0, Array.Empty<ChatMessageEvidence>()), 7, 10);

        Assert.Equal(expected, row.CanConnect);
        if (!expected) Assert.Null(row.VerifiedNetwork);
    }

    [Fact]
    public void RetryAfterTelegramError_RestoresUsableResult()
    {
        var row = new ChatAnalysisResultRow("15 Пост чат");
        row.SetError("Нет соединения", needsConnectionRetry: true);
        Assert.False(row.CanConnect);
        Assert.True(row.NeedsConnectionRetry);

        row.SetResult(new ChatActivityAnalysis(ChatActivityClassification.POSSIBLE_ACTIVITY,
            ChatTaskNetwork.TELEGRAM, 1, 0, []), 1, 4);

        Assert.False(row.NeedsConnectionRetry);
        Assert.True(row.CanConnect);
        Assert.Equal("Success", row.StatusKind);
    }

    [Fact]
    public void ErrorAfterSuccessfulCheck_BlocksProceedingUntilRetry()
    {
        var row = new ChatAnalysisResultRow("15 Пост чат");
        row.SetResult(new ChatActivityAnalysis(ChatActivityClassification.POSSIBLE_ACTIVITY,
            ChatTaskNetwork.INSTAGRAM, 1, 0, []), 1, 4);
        Assert.True(row.CanConnect);

        row.SetError("Ошибка подключения", needsConnectionRetry: true);

        Assert.False(row.CanConnect);
        Assert.Null(row.VerifiedNetwork);
        Assert.True(row.NeedsConnectionRetry);
    }
}
