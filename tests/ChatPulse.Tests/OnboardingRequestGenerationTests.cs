using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class OnboardingRequestGenerationTests
{
    [Fact]
    public void NewerScreenRequest_InvalidatesOlderResults()
    {
        var generation = new OnboardingRequestGeneration();
        var oldRequest = generation.Advance();
        var latestRequest = generation.Advance();

        Assert.False(generation.IsCurrent(oldRequest));
        Assert.True(generation.IsCurrent(latestRequest));
    }

    [Fact]
    public void LeavingScreen_InvalidatesInFlightLoadWithoutCancellation()
    {
        var generation = new OnboardingRequestGeneration();
        var inFlight = generation.Advance();
        generation.Advance(); // View was hidden.

        Assert.False(generation.IsCurrent(inFlight));
    }

    [Fact]
    public async Task SlowOldRequest_CannotOverwriteNewerScreenResult()
    {
        var generation = new OnboardingRequestGeneration();
        var staleRequest = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = generation.Advance();
        var displayed = "";
        var applyOld = staleRequest.Task.ContinueWith(task =>
        {
            if (generation.IsCurrent(first)) displayed = task.Result;
        }, TaskScheduler.Default);
        var second = generation.Advance();
        if (generation.IsCurrent(second)) displayed = "Актуальные данные";

        staleRequest.SetResult("Данные старого аккаунта");
        await applyOld.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Актуальные данные", displayed);
    }

    [Fact]
    public void ZeroGeneration_CannotBeMistakenForActiveRequest()
        => Assert.False(new OnboardingRequestGeneration().IsCurrent(0));

    [Fact]
    public async Task ConcurrentAdvances_ProduceDistinctGenerationNumbers()
    {
        var generation = new OnboardingRequestGeneration();
        var requests = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run<long>(() => generation.Advance())));

        Assert.Equal(32, requests.Distinct().Count());
        Assert.True(generation.IsCurrent(requests.Max()));
    }
}
