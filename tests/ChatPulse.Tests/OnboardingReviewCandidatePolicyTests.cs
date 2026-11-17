using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class OnboardingReviewCandidatePolicyTests
{
    [Fact]
    public void ValidReview_PreservesExactOwnerPeerNetworkAndOrder()
    {
        var selected = new[] { Chat(101, "Пост чат Instagram"), Chat(202, "Telegram пост чат") };
        var results = new[] { Approved(selected[0].Title, ChatTaskNetwork.INSTAGRAM),
            Approved(selected[1].Title, ChatTaskNetwork.TELEGRAM) };

        var candidates = ChatReviewCandidatePolicy.CreateCandidates(123, selected, results);

        Assert.Equal(new long[] { 101, 202 }, candidates.Select(c => c.PeerId));
        Assert.All(candidates, c => Assert.Equal(123L, c.TelegramUserId));
        Assert.Equal(new[] { "Instagram", "Telegram" }, candidates.Select(c => c.SocialNetwork));
        Assert.Equal(new[] { "Пост чат Instagram", "Telegram пост чат" },
            candidates.Select(c => c.TelegramTitle));
        Assert.All(candidates, c => Assert.Equal(7, c.HistoryDays));
        Assert.All(candidates, c => Assert.Equal("Channel", c.PeerType));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MissingAccountId_NeverCreatesPendingConnections(long owner)
        => Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(owner,
            [Chat(101, "Пост чат")], [Approved("Пост чат", ChatTaskNetwork.INSTAGRAM)]));

    [Fact]
    public void MismatchedResultCount_NeverSilentlyConnectsTruncatedZip()
        => Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(123,
            [Chat(101, "Пост чат 1"), Chat(202, "Пост чат 2")],
            [Approved("Пост чат 1", ChatTaskNetwork.INSTAGRAM)]));

    [Fact]
    public void ResultBelongingToAnotherSelectedChat_RejectsWholeReview()
    {
        var selected = new[] { Chat(101, "Пост чат 1"), Chat(202, "Пост чат 2") };
        var results = new[] { Approved("Пост чат 2", ChatTaskNetwork.INSTAGRAM),
            Approved("Пост чат 1", ChatTaskNetwork.TELEGRAM) };

        Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(123, selected, results));
    }

    [Fact]
    public void IdenticalChatTitles_CannotSwapAnalyzedPeerIds()
    {
        var selected = new[] { Chat(101, "Пост чат"), Chat(202, "Пост чат") };
        var first = new ChatAnalysisResultRow("Пост чат", 202, "Channel");
        first.SetResult(new ChatActivityAnalysis(ChatActivityClassification.POSSIBLE_ACTIVITY,
            ChatTaskNetwork.INSTAGRAM, 1, 0, []), 7, 20);
        var second = new ChatAnalysisResultRow("Пост чат", 101, "Channel");
        second.SetResult(new ChatActivityAnalysis(ChatActivityClassification.POSSIBLE_ACTIVITY,
            ChatTaskNetwork.TELEGRAM, 1, 0, []), 7, 20);

        Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(123, selected, [first, second]));
    }

    [Fact]
    public void SamePeerIdWithDifferentType_CannotUseAnotherChatsAnalysis()
    {
        var row = new ChatAnalysisResultRow("Пост чат", 101, "Chat");
        row.SetResult(new ChatActivityAnalysis(ChatActivityClassification.POSSIBLE_ACTIVITY,
            ChatTaskNetwork.INSTAGRAM, 1, 0, []), 7, 20);

        Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(123,
            [Chat(101, "Пост чат")], [row]));
    }

    [Fact]
    public void DuplicatedPeer_InSelectionIsNotConnectedTwice()
    {
        var chat = Chat(101, "Пост чат 1");
        var row = Approved(chat.Title, ChatTaskNetwork.INSTAGRAM);

        Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(123, [chat, chat], [row, row]));
    }

    [Theory]
    [InlineData("Group")]
    [InlineData("Репосты")]
    [InlineData("Постчат")]
    public void IneligibleName_DoesNotBypassStrictTitleRule(string title)
        => Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(123,
            [Chat(101, title)], [Approved(title, ChatTaskNetwork.INSTAGRAM)]));

    [Fact]
    public void NonRecognizedChat_DoesNotPreventAnotherVerifiedChat()
    {
        var first = Chat(101, "Пост чат Instagram");
        var second = Chat(202, "Пост чат Telegram");
        var unrecognized = new ChatAnalysisResultRow(second.Title);
        unrecognized.SetError("Нет связи", needsConnectionRetry: true);

        var result = ChatReviewCandidatePolicy.CreateCandidates(123, [first, second],
            [Approved(first.Title, ChatTaskNetwork.INSTAGRAM), unrecognized]);

        Assert.Equal(101L, Assert.Single(result).PeerId);
    }

    [Fact]
    public void ConnectedResult_IsNotProposedTwice()
    {
        var row = Approved("Пост чат", ChatTaskNetwork.INSTAGRAM);
        row.MarkConnected();

        Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(123,
            [Chat(101, "Пост чат")], [row]));
    }

    [Fact]
    public void UnknownNetwork_IsNotMisclassifiedAsTelegram()
        => Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(123,
            [Chat(101, "Пост чат")], [Approved("Пост чат", (ChatTaskNetwork)999)]));

    [Fact]
    public void MoreThanTenCandidates_CannotBypassSelectionLimit()
    {
        var selected = Enumerable.Range(1, 11)
            .Select(id => Chat(id, $"Пост чат {id}")).ToArray();
        var results = selected.Select(chat => Approved(chat.Title, ChatTaskNetwork.INSTAGRAM))
            .ToArray();

        Assert.Empty(ChatReviewCandidatePolicy.CreateCandidates(123, selected, results));
    }

    [Fact]
    public void SelectionIsCurrent_RequiresMatchingPeerTypeAndTitle()
    {
        var selected = new[] { Chat(101, "Пост чат") };
        Assert.True(ChatReviewCandidatePolicy.AreSelectionsCurrent(selected, selected));
        Assert.False(ChatReviewCandidatePolicy.AreSelectionsCurrent(selected, [Chat(101, "Другой пост чат")]));
        Assert.False(ChatReviewCandidatePolicy.AreSelectionsCurrent(selected,
            [new TelegramChatOption(101, "Chat", "Пост чат", "Группа")]));
        Assert.False(ChatReviewCandidatePolicy.AreSelectionsCurrent(selected, []));
    }

    [Fact]
    public void CurrentSelection_RemainsValidWhenUnrelatedChatIsAdded()
        => Assert.True(ChatReviewCandidatePolicy.AreSelectionsCurrent(
            [Chat(101, "Пост чат 1")], [Chat(101, "Пост чат 1"), Chat(202, "Пост чат 2")]));

    [Fact]
    public void NullLists_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ChatReviewCandidatePolicy.CreateCandidates(123,
            null!, Array.Empty<ChatAnalysisResultRow>()));
        Assert.Throws<ArgumentNullException>(() => ChatReviewCandidatePolicy.CreateCandidates(123,
            Array.Empty<TelegramChatOption>(), null!));
        Assert.Throws<ArgumentNullException>(() => ChatReviewCandidatePolicy.AreSelectionsCurrent(
            null!, Array.Empty<TelegramChatOption>()));
    }

    private static TelegramChatOption Chat(long id, string title)
        => new(id, "Channel", title, "Канал");

    private static ChatAnalysisResultRow Approved(string title, ChatTaskNetwork network)
    {
        var row = new ChatAnalysisResultRow(title);
        row.SetResult(new ChatActivityAnalysis(ChatActivityClassification.POSSIBLE_ACTIVITY,
            network, 1, 0, []), 7, 20);
        return row;
    }
}
