using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class SynchronizationScreenPhaseTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    public void WithoutImporting_ConfigurationIsVisible(int total, int complete)
    {
        Assert.Equal(SynchronizationScreenPhase.CONFIGURATION,
            SynchronizationScreenPhaseResolver.Resolve(total, complete, false));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void BusyState_AlwaysShowsProgress(int total, int complete)
    {
        Assert.Equal(SynchronizationScreenPhase.SYNCHRONIZING,
            SynchronizationScreenPhaseResolver.Resolve(total, complete, true));
    }

    [Fact]
    public void CompletedHistoryImport_ShowsImportResultNotOnboardingCompletion()
    {
        Assert.Equal(SynchronizationScreenPhase.HISTORY_RESULT,
            SynchronizationScreenPhaseResolver.Resolve(3, 3, false));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, -1)]
    [InlineData(1, 2)]
    public void InvalidChatCounts_AreRejected(int total, int completed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SynchronizationScreenPhaseResolver.Resolve(total, completed, false));
    }
}
