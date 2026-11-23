using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramFollowTaskVerificationTests
{
    [Theory]
    [InlineData("Подписки", InstagramFollowButtonState.FOLLOWING)]
    [InlineData("Following", InstagramFollowButtonState.FOLLOWING)]
    [InlineData("Отписаться", InstagramFollowButtonState.FOLLOWING)]
    [InlineData("Подписаться", InstagramFollowButtonState.NOT_FOLLOWING)]
    [InlineData("Follow", InstagramFollowButtonState.NOT_FOLLOWING)]
    [InlineData("Запрос отправлен", InstagramFollowButtonState.UNKNOWN)]
    [InlineData("Подписки 23", InstagramFollowButtonState.UNKNOWN)]
    public void Classify_OnlyExactProfileButtonStates(string label, InstagramFollowButtonState expected)
        => Assert.Equal(expected, InstagramFollowTaskRules.Classify([label]));

    [Fact]
    public void Classify_ContradictoryStatesAreNotConfirmed()
        => Assert.Equal(InstagramFollowButtonState.UNKNOWN,
            InstagramFollowTaskRules.Classify(["Подписки", "Подписаться"]));

    [Fact]
    public void Classify_EmptyLabelsAreNotConfirmed()
        => Assert.Equal(InstagramFollowButtonState.UNKNOWN,
            InstagramFollowTaskRules.Classify([]));

    [Fact]
    public void GetProfileTargets_UsesOnlyFirstProfile_NotActivityProfile()
    {
        var text = """
            **ВНИМАНИЕ‼️‼️**
            🩵**УЧАСТНИЦА ПРОШЛА АДАПТАЦИЮ 🩵**
            [https://www.instagram.com/q.r1s\_](https://www.instagram.com/q.r1s_)
            Подписка сюда 👆🏼
            Актив дает отсюда 👇🏼
            [https://www.instagram.com/\_\_dark__storona\_\_](https://www.instagram.com/__dark__storona__)
            **✅ПОДПИСКА✅**
            """;

        Assert.Equal(WorkspaceTaskKind.SUBSCRIBE, WorkspaceTaskKindDetector.Detect(text));
        Assert.Equal(new[] { "q.r1s_" },
            InstagramFollowTaskRules.GetProfileTargets(text, "https://www.instagram.com/__dark__storona__/"));
    }

    [Fact]
    public void GetProfileTargets_PreservesMessageOrder_InsteadOfSorting()
    {
        var targets = InstagramFollowTaskRules.GetProfileTargets(
            "Подписка https://www.instagram.com/z.user/ " +
            "Актив отсюда https://www.instagram.com/a.user/ " +
            "https://www.instagram.com/reel/C12345/", null);
        Assert.Equal(new[] { "z.user" }, targets);
    }

    [Fact]
    public void GetProfileTargets_IgnoresPostLinksAndUsesFirstProfile()
    {
        var targets = InstagramFollowTaskRules.GetProfileTargets(
            "https://www.instagram.com/reel/C12345/ " +
            "Подписка https://www.instagram.com/first.user/ " +
            "Актив https://www.instagram.com/second.user/", null);
        Assert.Equal(new[] { "first.user" }, targets);
    }

    [Fact]
    public void GetProfileTargets_UsesLegacyProfileUrl_WhenMessageHasNoProfile()
    {
        Assert.Equal(new[] { "legacy.user" }, InstagramFollowTaskRules.GetProfileTargets(
            "Подписка", "https://www.instagram.com/legacy.user/"));
        Assert.Empty(InstagramFollowTaskRules.GetProfileTargets(
            "Подписка", "https://www.instagram.com/p/Abc123/"));
    }

    [Fact]
    public void CanHide_RequiresConfirmedResult()
    {
        var negative = new InstagramFollowTaskVerification("owner", "target", WorkspaceTaskKind.UNSUBSCRIBE,
            InstagramTaskCheckResult.Inconclusive("Нет надёжного состояния кнопки."));
        var positive = negative with { Check = InstagramTaskCheckResult.Confirmed("Не подписан") };
        Assert.False(negative.CanHide);
        Assert.True(positive.CanHide);
    }
    [Fact]
    public void MassUnsubscribe_ChecksEveryUniqueProfileInOriginalOrder()
    {
        var text = """
            **МАССОВАЯ ОТПИСКА**
            https://www.instagram.com/z.profile/
            https://www.instagram.com/a.profile/?hl=ru
            https://www.instagram.com/Z.Profile/
            https://www.instagram.com/reel/Df9ollSMsmL/
            https://www.instagram.com/m.profile/
            """;
        Assert.Equal(WorkspaceTaskKind.UNSUBSCRIBE, WorkspaceTaskKindDetector.Detect(text));
        Assert.Equal(new[] { "z.profile", "a.profile", "m.profile" },
            InstagramFollowTaskRules.GetVerificationTargets(WorkspaceTaskKind.UNSUBSCRIBE, text, null));
        Assert.Equal(new[] { "z.profile" },
            InstagramFollowTaskRules.GetVerificationTargets(WorkspaceTaskKind.SUBSCRIBE, text, null));
    }

    [Fact]
    public async Task MassUnsubscribe_VerifiesInOrderAndDoesNotHideIfOneStillFollowed()
    {
        var visited = new List<string>();
        var result = await InstagramFollowTaskRules.VerifyTargetsAsync("owner", WorkspaceTaskKind.UNSUBSCRIBE,
            ["first", "second", "third"], (name, shouldFollow, _) =>
            {
                Assert.False(shouldFollow);
                visited.Add(name);
                return Task.FromResult(name == "second"
                    ? InstagramTaskCheckResult.Inconclusive("Подписка ещё есть")
                    : InstagramTaskCheckResult.Confirmed("Нет подписки"));
            });

        Assert.Equal(new[] { "first", "second", "third" }, visited);
        Assert.False(result.CanHide);
        Assert.Equal(3, result.Results.Count);
        var report = WorkspaceFailedTaskReport.FromFollow(result);
        Assert.True(report.HasFailure);
        Assert.Collection(report.Checks,
            check => { Assert.Contains("@first", check.Name); Assert.True(check.Confirmed); },
            check => { Assert.Contains("@second", check.Name); Assert.False(check.Confirmed); },
            check => { Assert.Contains("@third", check.Name); Assert.True(check.Confirmed); });
    }

    [Fact]
    public async Task MassUnsubscribe_AfterTechnicalError_StillChecksRemainingTargets()
    {
        var visited = new List<string>();
        var result = await InstagramFollowTaskRules.VerifyTargetsAsync("owner", WorkspaceTaskKind.UNSUBSCRIBE,
            ["a", "b", "c"], (name, _, _) =>
            {
                visited.Add(name);
                if (name == "b") throw new InvalidOperationException("network");
                return Task.FromResult(InstagramTaskCheckResult.Confirmed("Отписка есть"));
            });
        Assert.Equal(new[] { "a", "b", "c" }, visited);
        Assert.False(result.CanHide);
        Assert.Contains("network", result.Results[1].Check.Detail);
    }

    [Fact]
    public async Task MassUnsubscribe_AllConfirmed_CanHideOnlyAfterLastProfile()
    {
        var visited = new List<string>();
        var result = await InstagramFollowTaskRules.VerifyTargetsAsync("owner", WorkspaceTaskKind.UNSUBSCRIBE,
            ["first", "second"], (name, _, _) =>
            {
                visited.Add(name);
                return Task.FromResult(InstagramTaskCheckResult.Confirmed("Нет подписки"));
            });
        Assert.True(result.CanHide);
        Assert.Equal(new[] { "first", "second" }, visited);
        Assert.False(WorkspaceFailedTaskReport.FromFollow(result).HasFailure);
    }

    [Fact]
    public async Task Subscription_StillChecksOnlyFirstProfile()
    {
        var targets = InstagramFollowTaskRules.GetVerificationTargets(WorkspaceTaskKind.SUBSCRIBE,
            "Подписка https://www.instagram.com/first/ Актив отсюда https://www.instagram.com/second/", null);
        Assert.Single(targets);
        var result = await InstagramFollowTaskRules.VerifyTargetsAsync("owner", WorkspaceTaskKind.SUBSCRIBE,
            targets, (_, shouldFollow, _) =>
            {
                Assert.True(shouldFollow);
                return Task.FromResult(InstagramTaskCheckResult.Confirmed("Подписка есть"));
            });
        Assert.True(result.CanHide);
        Assert.Single(result.Results);
    }

}
