using ChatPulse.Application.Features.Chats.Analyze;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatActivityAnalyzerTests
{
    [Fact]
    public void InstagramReelAndPostInOneMessage_AreOneTask()
    {
        var result = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(10, "https://www.instagram.com/reel/abc123/ Или https://www.instagram.com/p/abc123/ Лайк, репост")
        ]);

        Assert.Equal(ChatActivityClassification.POSSIBLE_ACTIVITY, result.Classification);
        Assert.Equal(ChatTaskNetwork.INSTAGRAM, result.Network);
        Assert.Equal(1, result.TaskCount);
        Assert.Equal(2, result.Messages[0].PostLinks.Count);
    }

    [Fact]
    public void InstagramProfileInModeratorList_IsNotTask()
    {
        var result = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(11,
            "Модератор Виктория https://www.instagram.com/example"));
        Assert.Equal(ChatMessageClassification.NOT_TASK, result.Classification);
    }

    [Fact]
    public void TelegramPublicationWithReactions_IsTask()
    {
        var result = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(12,
            "https://t.me/example_channel/1701 Просмотры вверх/вниз + реакции"));
        Assert.Equal(ChatMessageClassification.TASK, result.Classification);
        Assert.Equal(ChatTaskNetwork.TELEGRAM, result.Network);
    }

    [Fact]
    public void BarePostLink_IsOnlyPossibleTask()
    {
        var result = ChatActivityAnalyzer.AnalyzeMessage(new ChatActivityMessage(13,
            "https://www.instagram.com/p/abc123/ Спасибо ❤️"));
        Assert.Equal(ChatMessageClassification.POSSIBLE_TASK, result.Classification);
    }

    [Fact]
    public void DifferentNetworks_DoNotChooseNetworkArbitrarily()
    {
        var result = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(21, "https://www.instagram.com/p/post1/ Лайк"),
            new ChatActivityMessage(22, "https://t.me/channel/123 Реакции")
        ]);
        Assert.Equal(ChatActivityClassification.CONFLICTING_NETWORKS, result.Classification);
        Assert.Null(result.Network);
    }

    [Fact]
    public void OnlyRules_NoActivityConfirmation()
    {
        var result = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(31, "Это наш пост чат! Сюда кидаем ссылки для актива"),
            new ChatActivityMessage(32, "VIP участники https://www.instagram.com/example")
        ]);
        Assert.Equal(ChatActivityClassification.INSUFFICIENT_DATA, result.Classification);
        Assert.Null(result.Network);
    }
}
