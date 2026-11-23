using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTaskKindAndLinksTests
{
    [Theory]
    [InlineData("ПОДПИСКА 💜 https://instagram.com/user", WorkspaceTaskKind.SUBSCRIBE)]
    [InlineData("⚡ ОТПИСКА ⚡ https://instagram.com/user", WorkspaceTaskKind.UNSUBSCRIBE)]
    [InlineData("О💔Т💔П💔И💔С💔К💔А https://instagram.com/username", WorkspaceTaskKind.UNSUBSCRIBE)]
    [InlineData("о т п и с к а https://instagram.com/username", WorkspaceTaskKind.UNSUBSCRIBE)]
    [InlineData("Моя публикация https://instagram.com/p/example", WorkspaceTaskKind.TASK)]
    [InlineData("", WorkspaceTaskKind.TASK)]
    public void ClassifiesTaskTextForPresentation(string text, WorkspaceTaskKind expected)
        => Assert.Equal(expected, WorkspaceTaskKindDetector.Detect(text));

    [Fact]
    public void UnsubscribeTakesPriorityOverSubscribe()
        => Assert.Equal(WorkspaceTaskKind.UNSUBSCRIBE,
            WorkspaceTaskKindDetector.Detect("Подписка, потом отписка https://instagram.com/username"));

    [Fact]
    public void EngagementPostWithSubscribeInstructionButNoProfileIsRegularTask()
    {
        var message = """
            **VIP LITE PLUS**⭐️
            [https://www.instagram.com/reel/Df9ollSMsmL/?cnvx=MXU3YTYzb2I5dDZzcw==](https://www.instagram.com/reel/Df9ollSMsmL/?cnvx=MXU3YTYzb2I5dDZzcw==)
            [https://www.instagram.com/p/Df9ollSMsmL/?cnvx=MXU3YTYzb2I5dDZzcw==](https://www.instagram.com/p/Df9ollSMsmL/?cnvx=MXU3YTYzb2I5dDZzcw==)
            Подписка✔️
            🤍лайк
            🤍репост
            🤍комментарий
            🤍сохранение
            **VIP LITE PLUS**⭐️
            🤍Контролируется администрацией чата🤍
            """;
        var postUrl = "https://www.instagram.com/p/Df9ollSMsmL/";

        Assert.Equal(WorkspaceTaskKind.TASK, WorkspaceTaskKindDetector.Detect(message, postUrl));
        var card = new ChatPulse.Desktop.Views.Workspace.WorkspaceTaskCard(
            new WorkspaceDetectedTask(10, message, postUrl));
        Assert.Equal("Задание", card.KindLabel);
        Assert.True(card.IsRegularTask);
        Assert.False(card.IsSubscriptionAction);
        Assert.Equal(2, card.ActionLinks.Count);
    }

    [Theory]
    [InlineData("Подписка✔️ https://www.instagram.com/reel/Df9ollSMsmL/", WorkspaceTaskKind.TASK)]
    [InlineData("Отписка✔️ https://www.instagram.com/p/Df9ollSMsmL/", WorkspaceTaskKind.TASK)]
    [InlineData("Подписка без URL", WorkspaceTaskKind.TASK)]
    [InlineData("ПОДПИСКА https://www.instagram.com/q.r1s_/", WorkspaceTaskKind.SUBSCRIBE)]
    [InlineData("ОТПИСКА https://www.instagram.com/q.r1s_/", WorkspaceTaskKind.UNSUBSCRIBE)]
    [InlineData("Подписка https://www.instagram.com/reel/Df9ollSMsmL/ https://www.instagram.com/q.r1s_/", WorkspaceTaskKind.SUBSCRIBE)]
    public void FollowTasksRequireAnActualInstagramProfileUrl(string message, WorkspaceTaskKind expected)
        => Assert.Equal(expected, WorkspaceTaskKindDetector.Detect(message));

    [Fact]
    public void SubscriptionCanUseLegacyProfileUrlButNotLegacyPostUrl()
    {
        Assert.Equal(WorkspaceTaskKind.SUBSCRIBE, WorkspaceTaskKindDetector.Detect(
            "Подписка", "https://www.instagram.com/q.r1s_/"));
        Assert.Equal(WorkspaceTaskKind.TASK, WorkspaceTaskKindDetector.Detect(
            "Подписка", "https://www.instagram.com/reel/Df9ollSMsmL/"));
    }

    [Fact]
    public void ConvertsLinksToShortLabelsWithoutChangingTheirTargets()
    {
        var source = "Ссылка: https://www.instagram.com/profile_x/; чат https://t.me/example.";
        var parts = WorkspaceTaskLinks.Split(source);
        Assert.Contains(parts, part => part.Text == "Открыть Instagram" &&
            part.Url == "https://www.instagram.com/profile_x/");
        Assert.Contains(parts, part => part.Text == "Открыть Telegram" &&
            part.Url == "https://t.me/example");
        Assert.Contains(parts, part => part.Text == "; чат " && part.Url is null);
        Assert.EndsWith(".", parts[^1].Text);
    }

    [Theory]
    [InlineData("☑️ПОДПИСКА☑️\nВНИМАНИЕ\nhttps://www.instagram.com/user_one/", true, "#F2FBF5")]
    [InlineData("О💔Т💔П💔И💔С💔К💔А https://www.instagram.com/user_two/", true, "#FFF6EC")]
    [InlineData("Лайк https://www.instagram.com/p/example/", false, "#FCFAFF")]
    public void SubscriptionCardsOnlyExposeLinksAndUseDistinctBackgrounds(
        string text, bool subscriptionAction, string background)
    {
        var card = new ChatPulse.Desktop.Views.Workspace.WorkspaceTaskCard(
            new ChatPulse.Desktop.Services.WorkspaceDetectedTask(10, text, ""));
        Assert.Equal(subscriptionAction, card.IsSubscriptionAction);
        Assert.Equal(!subscriptionAction, card.IsRegularTask);
        Assert.Equal(background, card.CardBackground);
        if (subscriptionAction)
        {
            var link = Assert.Single(card.ActionLinks);
            Assert.StartsWith("Открыть Instagram", link.Text);
            Assert.StartsWith("https://www.instagram.com/", link.Url);
        }
    }

    [Fact]
    public void SubscriptionCardKeepsEveryDistinctLinkAndNeverIncludesInstructionText()
    {
        var card = new ChatPulse.Desktop.Views.Workspace.WorkspaceTaskCard(
            new ChatPulse.Desktop.Services.WorkspaceDetectedTask(10,
                "ПОДПИСКА\nhttps://instagram.com/first/\nhttps://instagram.com/second/", ""));
        Assert.True(card.IsSubscriptionAction);
        Assert.Equal(2, card.ActionLinks.Count);
        Assert.All(card.ActionLinks, part => Assert.NotNull(part.Url));
        Assert.DoesNotContain(card.ActionLinks, part => part.Text.Contains("ПОДПИСКА"));
    }

    [Theory]
    [InlineData("ПОДПИСКА https://instagram.com/a", "Подписка", "#D9F1E0", "#246F43")]
    [InlineData("О💔Т💔П💔И💔С💔К💔А https://instagram.com/b", "Отписка", "#FFE5CC", "#965219")]
    public void SubscriptionCardsKeepVisibleKindLabelAndMatchingBadgeColors(
        string text, string expectedLabel, string expectedBackground, string expectedForeground)
    {
        var card = new ChatPulse.Desktop.Views.Workspace.WorkspaceTaskCard(
            new ChatPulse.Desktop.Services.WorkspaceDetectedTask(10, text, ""));
        Assert.True(card.IsSubscriptionAction);
        Assert.Equal(expectedLabel, card.KindLabel);
        Assert.Equal(expectedBackground, card.KindBackground);
        Assert.Equal(expectedForeground, card.KindForeground);
        Assert.Single(card.ActionLinks);
    }

    [Fact]
    public void PlainTextIsNotTurnedIntoLink()
    {
        var part = Assert.Single(WorkspaceTaskLinks.Split("Подписка без URL"));
        Assert.Null(part.Url);
        Assert.Equal("Подписка без URL", part.Text);
    }
}
