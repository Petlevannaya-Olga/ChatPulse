using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTaskExecutionProgressTests
{
    [Fact]
    public void FreshSession_StartsFromRemainingTasks()
    {
        var initial = new WorkspaceTaskExecutionProgress(12, 0);
        var afterFour = new WorkspaceTaskExecutionProgress(12, 4);
        var reopened = new WorkspaceTaskExecutionProgress(8, 0);

        Assert.Equal(12, initial.Remaining);
        Assert.Equal(1, initial.CurrentNumber);
        Assert.Equal(8, afterFour.Remaining);
        Assert.Equal(5, afterFour.CurrentNumber);
        Assert.Equal(8, reopened.Remaining);
        Assert.Equal(1, reopened.CurrentNumber);
        Assert.Equal(0, reopened.Completed);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 1)]
    [InlineData(3, 0, 3, 1, 0)]
    [InlineData(3, 2, 1, 3, 2.0 / 3.0)]
    [InlineData(3, 3, 0, 0, 1)]
    public void Progress_TracksCompletedAndRemaining(int total, int done,
        int expectedRemaining, int expectedCurrent, double expectedFraction)
    {
        var progress = new WorkspaceTaskExecutionProgress(total, done);
        Assert.Equal(expectedRemaining, progress.Remaining);
        Assert.Equal(expectedCurrent, progress.CurrentNumber);
        Assert.Equal(expectedFraction, progress.Fraction, 9);
    }
}
