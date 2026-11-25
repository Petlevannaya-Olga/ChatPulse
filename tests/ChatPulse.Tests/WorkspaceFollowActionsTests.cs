using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceFollowActionsTests
{
    [Fact]
    public void MixedAnnouncement_KeepsKindsAndOrder_DeduplicatesMarkdownLinks()
    {
        const string text = """
            1. К нам присоединилась и уде прошла адаптацию, прошу ДАТЬ ВЗАИМНУЮ ПОДПИСКУ 👇
            [https://www.instagram.com/olgablyum2025](https://www.instagram.com/olgablyum2025)
            Наш чат ПОКИНУЛИ 👇
            1. [https://www.instagram.com/biserom_risuyu](https://www.instagram.com/biserom_risuyu)
            2. [https://www.instagram.com/t.iono_39](https://www.instagram.com/t.iono_39)
            Всем хорошего вечера и до завтра!
            """;
        Assert.Equal(WorkspaceTaskKind.MIXED, WorkspaceTaskKindDetector.Detect(text));
        Assert.Equal(new[]
        {
            new WorkspaceFollowAction(WorkspaceTaskKind.SUBSCRIBE, "olgablyum2025"),
            new WorkspaceFollowAction(WorkspaceTaskKind.UNSUBSCRIBE, "biserom_risuyu"),
            new WorkspaceFollowAction(WorkspaceTaskKind.UNSUBSCRIBE, "t.iono_39")
        }, WorkspaceFollowActions.GetActions(text, null));
    }

    [Fact]
    public void NewVipAndRemovedParticipant_AreSeparateActions_OptionalFollowNoteIgnored()
    {
        const string text = """
            ✨✨✨ СЕГОДНЯ ✨✨✨
            1. У нас НОВЫЙ ВИП, ПРОШУ ПОДПИСАТЬСЯ ⤵️
            [https://www.instagram.com/_osobennaya_____](https://www.instagram.com/_osobennaya_____)
            2. Удалена из чата за нарушение правил (не даёт полноценный актив) и игнорирование администрации 👇
            [https://www.instagram.com/mami._ira](https://www.instagram.com/mami._ira)
            ❌ Подписка по желанию ❌
            Всем хорошего вечера и до завтра!
            """;
        var expected = new[]
        {
            new WorkspaceFollowAction(WorkspaceTaskKind.SUBSCRIBE, "_osobennaya_____"),
            new WorkspaceFollowAction(WorkspaceTaskKind.UNSUBSCRIBE, "mami._ira")
        };

        Assert.True(WorkspaceFollowActions.TryParseAnnouncement(text, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(WorkspaceTaskKind.MIXED, WorkspaceTaskKindDetector.Detect(text));
        Assert.Equal(expected, WorkspaceFollowActions.GetActions(text, null));

        var card = new ChatPulse.Desktop.Views.Workspace.WorkspaceTaskCard(
            new WorkspaceDetectedTask(101, text, ""));
        Assert.True(card.HasSubscribeActions);
        Assert.True(card.HasUnsubscribeActions);
        Assert.Collection(card.FollowActionLinks,
            join =>
            {
                Assert.Equal("@_osobennaya_____", join.Text);
                Assert.True(join.IsSubscription);
            },
            leave =>
            {
                Assert.Equal("@mami._ira", leave.Text);
                Assert.False(leave.IsSubscription);
            });
    }

    [Theory]
    [InlineData("Удалена из чата за нарушение правил https://www.instagram.com/leaver", "leaver")]
    [InlineData("Исключена из чата https://www.instagram.com/other_leaver", "other_leaver")]
    [InlineData("Наш чат ПОКИНУЛИ https://www.instagram.com/previous_leaver", "previous_leaver")]
    public void LeaveAnnouncements_AreUnsubscriptions(string text, string expectedUserName)
    {
        Assert.Equal(WorkspaceTaskKind.UNSUBSCRIBE, WorkspaceTaskKindDetector.Detect(text));
        Assert.Equal(new[] { new WorkspaceFollowAction(WorkspaceTaskKind.UNSUBSCRIBE, expectedUserName) },
            WorkspaceFollowActions.GetActions(text, null));
    }

    [Fact]
    public void OptionalFollowAlone_DoesNotBecomeAnnouncement()
    {
        const string text = "Подписка по желанию https://www.instagram.com/someone";
        Assert.False(WorkspaceFollowActions.TryParseAnnouncement(text, out _));
    }

    [Fact]
    public void MixedAnnouncement_CardShowsActionsWithCorrectLabels()
    {
        const string text = """
            Прошла адаптацию, прошу ДАТЬ ВЗАИМНУЮ ПОДПИСКУ
            https://www.instagram.com/joiner/
            Наш чат ПОКИНУЛИ
            https://www.instagram.com/leaver/
            """;
        var card = new ChatPulse.Desktop.Views.Workspace.WorkspaceTaskCard(
            new WorkspaceDetectedTask(42, text, ""));
        Assert.Equal("Подписка и отписка", card.KindLabel);
        Assert.True(card.IsSubscriptionAction);
        Assert.Collection(card.ActionLinks,
            join => Assert.Equal("Подписка @joiner", join.Text),
            leave => Assert.Equal("Отписка @leaver", leave.Text));
    }

    [Fact]
    public void MixedAnnouncement_CardDisplaysTwoColoredBadgesAndUsernamesOnly()
    {
        const string text = """
            К нам присоединилась и уже прошла адаптацию, прошу ДАТЬ ВЗАИМНУЮ ПОДПИСКУ
            https://www.instagram.com/joiner/
            Наш чат ПОКИНУЛИ
            https://www.instagram.com/leaver/
            https://www.instagram.com/another_leaver/
            """;
        var card = new ChatPulse.Desktop.Views.Workspace.WorkspaceTaskCard(
            new WorkspaceDetectedTask(99, text, ""));

        Assert.True(card.HasSubscribeActions);
        Assert.True(card.HasUnsubscribeActions);
        Assert.Collection(card.FollowActionLinks,
            join =>
            {
                Assert.Equal("@joiner", join.Text);
                Assert.Equal("https://www.instagram.com/joiner/", join.Url);
                Assert.True(join.IsSubscription);
                Assert.Equal("#EAF8EF", join.Background);
                Assert.Equal("#246F43", join.Foreground);
            },
            leave =>
            {
                Assert.Equal("@leaver", leave.Text);
                Assert.Equal("https://www.instagram.com/leaver/", leave.Url);
                Assert.False(leave.IsSubscription);
                Assert.Equal("#FFF2E4", leave.Background);
                Assert.Equal("#965219", leave.Foreground);
            },
            leave =>
            {
                Assert.Equal("@another_leaver", leave.Text);
                Assert.False(leave.IsSubscription);
            });
    }

    [Theory]
    [InlineData("Подписка https://instagram.com/joiner", true)]
    [InlineData("Отписка https://instagram.com/leaver", false)]
    public void SingleFollowAction_ShowsOnlyMatchingBadge(string text, bool isSubscription)
    {
        var card = new ChatPulse.Desktop.Views.Workspace.WorkspaceTaskCard(
            new WorkspaceDetectedTask(100, text, ""));
        Assert.Equal(isSubscription, card.HasSubscribeActions);
        Assert.Equal(!isSubscription, card.HasUnsubscribeActions);
        var link = Assert.Single(card.FollowActionLinks);
        Assert.Equal(isSubscription, link.IsSubscription);
    }

    [Fact]
    public void SingleJoinAnnouncement_IsOneSubscription()
    {
        const string text = """
            ✨✨✨ СЕГОДНЯ ✨✨✨
            К нам присоединилась и уже прошла адаптацию, прошу ДАТЬ ВЗАИМНУЮ ПОДПИСКУ 👇
            [https://www.instagram.com/broochkd](https://www.instagram.com/broochkd)
            Всем хорошего вечера и до завтра!
            """;
        Assert.Equal(WorkspaceTaskKind.SUBSCRIBE, WorkspaceTaskKindDetector.Detect(text));
        Assert.Equal(new[] { new WorkspaceFollowAction(WorkspaceTaskKind.SUBSCRIBE, "broochkd") },
            WorkspaceFollowActions.GetActions(text, null));
    }

    [Fact]
    public void TwoJoinAnnouncements_KeepBothTargetsAndIgnoreOwnAccount()
    {
        const string text = """
            ✨✨✨ СЕГОДНЯ ✨✨✨
            1 . Прошла адаптацию, прошу ДАТЬ ВЗАИМНУЮ ПОДПИСКУ 👇
            [https://www.instagram.com/mj.magic3d](https://www.instagram.com/mj.magic3d)
            2. К нам присоединилась и уже прошла адаптацию, прошу ДАТЬ ВЗАИМНУЮ ПОДПИСКУ 👇
            [https://www.instagram.com/soul_light_workshop](https://www.instagram.com/soul_light_workshop)
            Всем хорошего вечера и до завтра!
            """;
        var actions = WorkspaceFollowActions.GetActions(text, null);
        Assert.Equal(WorkspaceTaskKind.SUBSCRIBE, WorkspaceTaskKindDetector.Detect(text));
        Assert.Equal(new[] { "mj.magic3d", "soul_light_workshop" },
            actions.Select(action => action.TargetUserName));
        var filtered = WorkspaceFollowActions.WithoutOwnAccounts(actions,
            new InstagramOwnAccounts("soul_light_workshop", "olga.g.afonina"), "soul_light_workshop");
        Assert.Equal("mj.magic3d", Assert.Single(filtered).TargetUserName);
        Assert.Empty(WorkspaceFollowActions.WithoutOwnAccounts([
            new WorkspaceFollowAction(WorkspaceTaskKind.SUBSCRIBE, "olga.g.afonina")
        ], new InstagramOwnAccounts("soul_light_workshop", "olga.g.afonina"), "soul_light_workshop"));
    }

    [Fact]
    public void LegacyTask_StillUsesFirstProfileAndDoesNotTreatActivityAsAnotherSubscription()
    {
        const string text = """
            УЧАСТНИЦА ПРОШЛА АДАПТАЦИЮ
            Подписка сюда: https://www.instagram.com/first
            Актив дает отсюда: https://www.instagram.com/second
            ПОДПИСКА
            """;
        Assert.Equal(new[] { new WorkspaceFollowAction(WorkspaceTaskKind.SUBSCRIBE, "first") },
            WorkspaceFollowActions.GetActions(text, null));
    }

    [Fact]
    public void PublicationLink_OverridesAnnouncement()
    {
        const string text = """
            ДАТЬ ВЗАИМНУЮ ПОДПИСКУ
            https://www.instagram.com/auser/
            https://www.instagram.com/p/Df9ollSMsmL/
            """;
        Assert.Equal(WorkspaceTaskKind.TASK, WorkspaceTaskKindDetector.Detect(text));
        Assert.Empty(WorkspaceFollowActions.GetActions(text, null));
    }

    [Fact]
    public async Task MixedVerification_ChecksCorrectDirectionsAndDoesNotHidePartial()
    {
        var visited = new List<(string Name, bool ShouldFollow)>();
        var actions = new[]
        {
            new WorkspaceFollowAction(WorkspaceTaskKind.SUBSCRIBE, "joiner"),
            new WorkspaceFollowAction(WorkspaceTaskKind.UNSUBSCRIBE, "leaver")
        };
        var result = await InstagramFollowTaskRules.VerifyActionsAsync("owner", actions,
            (name, shouldFollow, _) =>
            {
                visited.Add((name, shouldFollow));
                return Task.FromResult(name == "leaver"
                    ? InstagramTaskCheckResult.Inconclusive("Подписка ещё есть")
                    : InstagramTaskCheckResult.Confirmed("Подписка есть"));
            });
        Assert.Equal(new[] { ("joiner", true), ("leaver", false) }, visited);
        Assert.Equal(WorkspaceTaskKind.MIXED, result.TaskKind);
        Assert.False(result.CanHide);
        var report = WorkspaceFailedTaskReport.FromFollow(result);
        Assert.Collection(report.Checks,
            first => Assert.StartsWith("Подписка @joiner", first.Name),
            second => Assert.StartsWith("Отписка @leaver", second.Name));
    }

    [Fact]
    public async Task MixedVerification_BrowserFailureStopsBeforeNextAction()
    {
        var visited = new List<string>();
        await Assert.ThrowsAsync<WorkspaceBulkTechnicalFailureException>(async () =>
            await InstagramFollowTaskRules.VerifyActionsAsync("owner", [
                new WorkspaceFollowAction(WorkspaceTaskKind.SUBSCRIBE, "joiner"),
                new WorkspaceFollowAction(WorkspaceTaskKind.UNSUBSCRIBE, "leaver"),
                new WorkspaceFollowAction(WorkspaceTaskKind.UNSUBSCRIBE, "later")
            ], (name, _, _) =>
            {
                visited.Add(name);
                if (name == "leaver") throw new InvalidOperationException("Chrome crashed");
                return Task.FromResult(InstagramTaskCheckResult.Confirmed("Подтверждено"));
            }, stopOnTechnicalFailure: true));
        Assert.Equal(new[] { "joiner", "leaver" }, visited);
    }
}
