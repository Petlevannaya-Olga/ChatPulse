using ChatPulse.Application.Features.Chats.Analyze;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>Step 3: message classification must never silently connect an ambiguous chat.</summary>
public sealed class OnboardingAnalysisCoverageTests
{
    [Fact]
    public void EmptyHistory_DoesNotApproveChat()
    {
        var analysis = ChatActivityAnalyzer.Analyze([]);

        Assert.Equal(ChatActivityClassification.INSUFFICIENT_DATA, analysis.Classification);
        Assert.Null(analysis.Network);
        Assert.Equal(0, analysis.TaskCount);
        Assert.Equal(0, analysis.PossibleTaskCount);
        Assert.Empty(analysis.Messages);
    }

    [Fact]
    public void NullHistory_ThrowsInsteadOfApprovingChat()
        => Assert.Throws<ArgumentNullException>(() => ChatActivityAnalyzer.Analyze(null!));

    [Theory]
    [InlineData("Лайк https://www.instagram.com/p/abc123/")]
    [InlineData("Реакции на https://www.instagram.com/reel/abc123/")]
    [InlineData("Комментируйте https://instagram.com/tv/abc123/")]
    public void InstagramPostWithAction_IsARecognizedTask(string body)
    {
        var result = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(10, body));

        Assert.Equal(10L, result.MessageId);
        Assert.Equal(ChatMessageClassification.TASK, result.Classification);
        Assert.Equal(ChatTaskNetwork.INSTAGRAM, result.Network);
        Assert.Single(result.PostLinks);
    }

    [Theory]
    [InlineData("Лайки https://t.me/example/42")]
    [InlineData("Реакции https://t.me/c/123/42")]
    public void TelegramPostWithAction_IsARecognizedTask(string body)
    {
        var result = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(10, body));

        Assert.Equal(ChatMessageClassification.TASK, result.Classification);
        Assert.Equal(ChatTaskNetwork.TELEGRAM, result.Network);
        Assert.Single(result.PostLinks);
    }

    [Theory]
    [InlineData("Лайк https://www.instagram.com/example/")]
    [InlineData("Реакции https://t.me/joinchat/123")]
    [InlineData("Лайк https://t.me/example/0")]
    [InlineData("Лайк https://t.me/example/no-id")]
    [InlineData("Поставьте лайк на пост")]
    [InlineData("https://example.com/p/abc123/ лайк")]
    public void InvalidOrNonPostLink_CannotCreateTask(string body)
    {
        var result = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(10, body));

        Assert.Equal(ChatMessageClassification.NOT_TASK, result.Classification);
        Assert.Null(result.Network);
        Assert.Empty(result.PostLinks);
    }

    [Fact]
    public void MissingMessageText_CannotCreateTask()
    {
        var result = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(101, null));

        Assert.Equal(101L, result.MessageId);
        Assert.Equal(ChatMessageClassification.NOT_TASK, result.Classification);
        Assert.Null(result.Network);
    }

    [Fact]
    public void BarePublicationLinks_StayPossibleAndDoNotApproveChat()
    {
        var analysis = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(1, "https://instagram.com/p/abc123/"),
            new ChatActivityMessage(2, "https://instagram.com/reel/def456/")
        ]);

        Assert.Equal(ChatActivityClassification.INSUFFICIENT_DATA, analysis.Classification);
        Assert.Equal(0, analysis.TaskCount);
        Assert.Equal(2, analysis.PossibleTaskCount);
        Assert.Null(analysis.Network);
    }

    [Fact]
    public void MultipleTasksOfOneNetwork_KeepOriginalMessageIds()
    {
        var analysis = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(101, "Лайк https://www.instagram.com/p/abc123/"),
            new ChatActivityMessage(203, "Комментарий https://www.instagram.com/reel/def456/"),
            new ChatActivityMessage(305, "Правила чата")
        ]);

        Assert.Equal(ChatActivityClassification.POSSIBLE_ACTIVITY, analysis.Classification);
        Assert.Equal(ChatTaskNetwork.INSTAGRAM, analysis.Network);
        Assert.Equal(2, analysis.TaskCount);
        Assert.Equal(new long[] { 101, 203, 305 }, analysis.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public void MixedNetworksInOneMessage_AreNotArbitrarilyAssigned()
    {
        var analysis = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(11, "Лайк https://www.instagram.com/p/abc123/ и реакция https://t.me/channel/42")
        ]);

        Assert.Equal(ChatActivityClassification.INSUFFICIENT_DATA, analysis.Classification);
        Assert.Null(analysis.Network);
        Assert.Equal(0, analysis.TaskCount);
        Assert.Equal(ChatMessageClassification.POSSIBLE_TASK, analysis.Messages[0].Classification);
    }

    [Fact]
    public void SeparateTasksFromDifferentNetworks_RequireAttention()
    {
        var analysis = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(11, "Лайк https://www.instagram.com/p/abc123/"),
            new ChatActivityMessage(12, "Реакции https://t.me/channel/42")
        ]);

        Assert.Equal(ChatActivityClassification.CONFLICTING_NETWORKS, analysis.Classification);
        Assert.Null(analysis.Network);
        Assert.Equal(2, analysis.TaskCount);
    }

    [Fact]
    public void OneRecognizedNetworkWithUnrelatedText_DoesNotCreateFalseConflict()
    {
        var analysis = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(11, "Лайк https://www.instagram.com/p/abc123/"),
            new ChatActivityMessage(12, "Объявление модератора. Без заданий.")
        ]);

        Assert.Equal(ChatActivityClassification.POSSIBLE_ACTIVITY, analysis.Classification);
        Assert.Equal(ChatTaskNetwork.INSTAGRAM, analysis.Network);
        Assert.Equal(1, analysis.TaskCount);
    }

    [Fact]
    public void DuplicateLinksInsideOneMessage_CountAsOneTask()
    {
        var analysis = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(91, "Лайк https://instagram.com/p/abc123/ и https://instagram.com/p/abc123/")
        ]);

        Assert.Equal(1, analysis.TaskCount);
        Assert.Equal(2, analysis.Messages[0].PostLinks.Count);
    }

    [Fact]
    public void LinksWithTerminalPunctuation_AreNormalized()
    {
        var message = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(42,
            "Лайк https://www.instagram.com/p/abc123/!"));

        Assert.Equal(ChatMessageClassification.TASK, message.Classification);
        Assert.Equal("https://www.instagram.com/p/abc123/", Assert.Single(message.PostLinks));
    }
}
