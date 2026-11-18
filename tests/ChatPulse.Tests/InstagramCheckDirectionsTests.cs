using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramCheckDirectionsTests
{
    [Theory]
    [InlineData(InstagramCheckDirections.NONE, false)]
    [InlineData(InstagramCheckDirections.FOLLOWERS, true)]
    [InlineData(InstagramCheckDirections.FOLLOWING, true)]
    [InlineData(InstagramCheckDirections.BOTH, true)]
    public void CheckDirectionsRequireAtLeastOneOption(InstagramCheckDirections value, bool expected)
        => Assert.Equal(expected, InstagramCheckDirectionSelection.IsValid(value));

    [Fact]
    public void SingleDirectionDoesNotIncludeOtherDirection()
    {
        string[] profiles = ["first", "second"];
        Assert.Empty(InstagramCheckDirectionSelection.Filter(profiles,
            InstagramCheckDirections.FOLLOWING, InstagramCheckDirections.FOLLOWERS));
        Assert.Equal(profiles, InstagramCheckDirectionSelection.Filter(profiles,
            InstagramCheckDirections.FOLLOWING, InstagramCheckDirections.FOLLOWING).ToArray());
    }

    [Fact]
    public void VipRowDoesNotOfferCheckOfSubscriptionToMe()
    {
        var vip = new InstagramSubscriptionRow(1, "VIP", "VIP", "vip_page",
            "https://www.instagram.com/vip_page/", "vip_page", "NOT_APPLICABLE", "UNVERIFIED", "—");
        var regular = vip with { Category = "Обычный", FollowsMeStatus = "UNVERIFIED" };
        Assert.False(vip.CanCheckFollowers);
        Assert.True(regular.CanCheckFollowers);
    }

    [Fact]
    public void OmittedDirectionDisplaysDashInsteadOfZero()
    {
        var result = new InstagramBatchResultSummary(DateTimeOffset.UtcNow,
            InstagramBatchOutcome.COMPLETED, 1, 1, 1, 0, 0, 0, 0,
            NotFoundFollowers: 0, NotFoundFollowing: 0,
            Directions: InstagramCheckDirections.FOLLOWING);
        Assert.Equal("—", result.NotFoundFollowersLabel);
        Assert.Equal("0", result.NotFoundFollowingLabel);
    }

    [Theory]
    [InlineData(InstagramBatchOutcome.STOPPED, true, false)]
    [InlineData(InstagramBatchOutcome.COMPLETED, false, true)]
    [InlineData(InstagramBatchOutcome.FAILED, false, true)]
    public void StoppedRunUsesCompactSummaryWhileOtherOutcomesKeepDetails(
        InstagramBatchOutcome outcome, bool isStopped, bool showDetailedResult)
    {
        var result = new InstagramBatchResultSummary(DateTimeOffset.UtcNow,
            outcome, 0, 81, 0, 0, 0, 17, 0);

        Assert.Equal(isStopped, result.IsStopped);
        Assert.Equal(showDetailedResult, result.ShowDetailedResult);
        Assert.Equal("Выполнено 0 из 81 поисковых проверок", result.ProgressLabel);
        Assert.Contains("последней успешной проверки", result.StoppedNotice);
    }
}
