using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Views.Workspace;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceStoryTaskRulesTests
{
    private const string Story = "https://www.instagram.com/stories/teplo_vata/4038231488120287484/";

    [Fact]
    public void ExampleMessage_IsRecognizedAsStoriesOnlyWithOneTarget()
    {
        const string message = """
            Добрый день!
            [https://www.instagram.com/stories/teplo_vata/4038231488120287484?utm_source=ig_story_item_share&cnvx=test](https://www.instagram.com/stories/teplo_vata/4038231488120287484?utm_source=ig_story_item_share&cnvx=test)
            Пожалуйста просмотр всех сторис с реакциями
            Ссылка на аккаунт:
            https://www.instagram.com/teplo_vata
            VIP organik
            """;

        Assert.Equal(WorkspaceTaskKind.STORY, WorkspaceTaskKindDetector.Detect(message));
        var onlyStories = WorkspaceStoryTaskRules.GetStoryOnlyUrls(message, string.Empty);
        Assert.Equal(Story, Assert.Single(onlyStories));
        var suggested = InstagramTaskVerificationRules.SuggestChecks(message);
        // Only Stories likes are selected by the Stories path, whatever the
        // generic publication suggestions would be.
        Assert.True(suggested.Like);
        var evidence = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(99, message));
        Assert.Equal(ChatMessageClassification.TASK, evidence.Classification);
        Assert.Equal(ChatTaskNetwork.INSTAGRAM, evidence.Network);
        Assert.Equal(Story.TrimEnd('/'), evidence.PostLinks[0].Split('?')[0]);

        var card = new WorkspaceTaskCard(new WorkspaceDetectedTask(99, message, Story));
        Assert.Equal("Сторис", card.KindLabel);
        Assert.Equal("↗  Сторис", card.StandalonePostUrlLabel);
        Assert.Equal(2, card.ActionLinks.Count); // story + profile, no duplicate story
        Assert.True(card.IsRegularTask);
    }

    [Fact]
    public void TwoStoriesWithSameMediaIdTwice_AreDeduplicated()
    {
        const string second = "https://www.instagram.com/stories/teplo_vata/4038231488120287555/";
        var message = $"{Story} {Story}?utm_source=telegram {second}";
        var stories = WorkspaceStoryTaskRules.GetStoryOnlyUrls(message, Story);
        Assert.Equal(new[] { Story, second }, stories);
        Assert.Equal(WorkspaceTaskKind.STORY, WorkspaceTaskKindDetector.Detect(message));
    }

    [Theory]
    [InlineData("https://www.instagram.com/stories/teplo_vata/")]
    [InlineData("https://www.instagram.com/stories/teplo_vata/abc123/")]
    [InlineData("https://notinstagram.com/stories/teplo_vata/4038231488120287484/")]
    [InlineData("http://www.instagram.com/stories/teplo_vata/4038231488120287484/")]
    [InlineData("https://www.instagram.com/stories/teplo_vata/4038231488120287484/next/")]
    public void InvalidOrAmbiguousStoryUrls_AreRejected(string link)
        => Assert.False(WorkspaceStoryTaskRules.TryNormalizeStoryUrl(link, out _));

    [Fact]
    public void VerificationCannotUseDifferentStoryOrAuthor()
    {
        Assert.True(WorkspaceStoryTaskRules.IsSameStoryUrl(
            Story + "?utm_source=test", Story));
        Assert.False(WorkspaceStoryTaskRules.IsSameStoryUrl(
            "https://www.instagram.com/stories/teplo_vata/4038231488120287485/", Story));
        Assert.False(WorkspaceStoryTaskRules.IsSameStoryUrl(
            "https://www.instagram.com/stories/other/4038231488120287484/", Story));
    }

    [Theory]
    [InlineData("https://www.instagram.com/p/Df9ollSMsmL/")]
    [InlineData("https://www.instagram.com/reel/Df9ollSMsmL/")]
    public void MixedStoryAndFeedPost_DoNotEnterStoriesOnlyVerification(string post)
    {
        var message = $"Лайк {Story} {post}";
        Assert.Equal(WorkspaceTaskKind.TASK, WorkspaceTaskKindDetector.Detect(message));
        Assert.Empty(WorkspaceStoryTaskRules.GetStoryOnlyUrls(message, string.Empty));
    }

    [Fact]
    public void ProfileAlone_DoesNotBecomeAStory()
    {
        const string message = "Лайк https://www.instagram.com/teplo_vata";
        Assert.Equal(WorkspaceTaskKind.TASK, WorkspaceTaskKindDetector.Detect(message));
        Assert.Empty(WorkspaceStoryTaskRules.GetStoryOnlyUrls(message, string.Empty));
    }

    [Fact]
    public void UnconfirmedStoryLike_CannotHideTask()
    {
        var result = new InstagramTaskVerificationResult("soul_light_workshop", Story,
            InstagramTaskCheckResult.Inconclusive("Сторис истекла."), null);
        Assert.False(result.CanHide);
        Assert.Null(result.Comment);
        Assert.Null(result.Save);
        Assert.Null(result.Repost);
    }
}
