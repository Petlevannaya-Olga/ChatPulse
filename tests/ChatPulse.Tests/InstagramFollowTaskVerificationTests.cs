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
}
