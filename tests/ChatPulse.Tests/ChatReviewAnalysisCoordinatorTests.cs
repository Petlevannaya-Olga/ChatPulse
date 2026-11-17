using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatReviewAnalysisCoordinatorTests
{
    [Fact]
    public async Task AllChats_AreReviewedInSelectionOrder_AndResultsAreAppliedToTheirPeers()
    {
        var selected = new[] { Chat(101, "Пост один"), Chat(202, "Пост два") };
        var rows = selected.Select(ResultRow).ToArray();
        var calls = new List<long>();
        var started = new List<int>();
        var notifications = 0;

        var finished = await new ChatReviewAnalysisCoordinator().RunAsync(selected, rows,
            (chat, _) =>
            {
                calls.Add(chat.PeerId);
                return Task.FromResult((Recognized(), 1, (int)chat.PeerId));
            },
            () => true,
            onChatStarted: started.Add,
            onResultChanged: () => notifications++);

        Assert.True(finished);
        Assert.Equal(new long[] { 101, 202 }, calls);
        Assert.Equal(new[] { 0, 1 }, started);
        Assert.Equal(2, notifications);
        Assert.All(rows, row => Assert.True(row.CanConnect));
        Assert.Contains("101", rows[0].Details);
        Assert.Contains("202", rows[1].Details);
    }

    [Fact]
    public async Task TelegramError_AffectsOnlyOneChat_AndLaterChatStillRuns()
    {
        var chats = new[] { Chat(101), Chat(202) };
        var rows = chats.Select(ResultRow).ToArray();
        var calls = new List<long>();

        var finished = await new ChatReviewAnalysisCoordinator().RunAsync(chats, rows,
            (chat, _) =>
            {
                calls.Add(chat.PeerId);
                if (chat.PeerId == 101)
                    throw new TelegramAuthenticationException("telegram.network_unavailable", "Связь потеряна");
                return Task.FromResult((Recognized(), 7, 12));
            }, () => true);

        Assert.True(finished);
        Assert.Equal(new long[] { 101, 202 }, calls);
        Assert.Equal("Error", rows[0].StatusKind);
        Assert.True(rows[0].NeedsConnectionRetry);
        Assert.True(rows[1].CanConnect);
    }

    [Fact]
    public async Task NonNetworkError_DoesNotBecomeConnectionRetry()
    {
        var chat = Chat(101);
        var row = ResultRow(chat);

        var finished = await new ChatReviewAnalysisCoordinator().RunAsync([chat], [row],
            (_, _) => throw new InvalidOperationException("test"), () => true);

        Assert.True(finished);
        Assert.Equal("Error", row.StatusKind);
        Assert.False(row.NeedsConnectionRetry);
        Assert.False(row.CanConnect);
    }

    [Fact]
    public async Task AutomaticRetry_OnlyRepeatsFailedConnectionChats()
    {
        var chats = new[] { Chat(101), Chat(202), Chat(303) };
        var rows = chats.Select(ResultRow).ToArray();
        rows[0].SetResult(Recognized(), 1, 3);
        rows[1].SetError("Сеть недоступна", needsConnectionRetry: true);
        rows[2].SetError("Ошибка содержимого");
        var calls = new List<long>();

        var finished = await new ChatReviewAnalysisCoordinator().RunAsync(chats, rows,
            (chat, _) =>
            {
                calls.Add(chat.PeerId);
                return Task.FromResult((Recognized(), 7, 10));
            }, () => true, retryFailedOnly: true);

        Assert.True(finished);
        Assert.Equal(new long[] { 202 }, calls);
        Assert.True(rows[0].CanConnect);
        Assert.True(rows[1].CanConnect);
        Assert.Equal("Error", rows[2].StatusKind);
    }

    [Fact]
    public async Task ManualRetry_IncludesOtherErrors_ButNotSuccessfulChats()
    {
        var chats = new[] { Chat(101), Chat(202), Chat(303) };
        var rows = chats.Select(ResultRow).ToArray();
        rows[0].SetResult(Recognized(), 1, 3);
        rows[1].SetError("Сеть недоступна", needsConnectionRetry: true);
        rows[2].SetError("Ошибка содержимого");
        var calls = new List<long>();

        var finished = await new ChatReviewAnalysisCoordinator().RunAsync(chats, rows,
            (chat, _) =>
            {
                calls.Add(chat.PeerId);
                return Task.FromResult((Recognized(), 7, 10));
            }, () => true, retryFailedOnly: true, includeOtherErrors: true);

        Assert.True(finished);
        Assert.Equal(new long[] { 202, 303 }, calls);
        Assert.All(rows, row => Assert.True(row.CanConnect));
    }

    [Fact]
    public async Task LateResponse_AfterNavigation_DoesNotUpdateOrContinueReview()
    {
        var chats = new[] { Chat(101), Chat(202) };
        var rows = chats.Select(ResultRow).ToArray();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<(ChatActivityAnalysis, int, int)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = true;
        var calls = 0;

        var running = new ChatReviewAnalysisCoordinator().RunAsync(chats, rows,
            async (_, _) =>
            {
                calls++;
                started.TrySetResult(true);
                return await response.Task;
            }, () => active);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        active = false; // User navigated away, but the Telegram RPC was not cancelled.
        response.SetResult((Recognized(), 1, 5));

        Assert.False(await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
        Assert.All(rows, row => Assert.Equal("Pending", row.StatusKind));
    }

    [Fact]
    public async Task Cancellation_AfterFirstRequest_SkipsRemainingPeers()
    {
        var chats = new[] { Chat(101), Chat(202) };
        var rows = chats.Select(ResultRow).ToArray();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;

        var finished = await new ChatReviewAnalysisCoordinator().RunAsync(chats, rows,
            (_, _) =>
            {
                calls++;
                cancellation.Cancel();
                return Task.FromResult((Recognized(), 1, 4));
            }, () => true, cancellationToken: cancellation.Token);

        Assert.False(finished);
        Assert.Equal(1, calls);
        Assert.All(rows, row => Assert.Equal("Pending", row.StatusKind));
    }

    [Fact]
    public async Task SameTitleDifferentPeer_RejectsMisalignedRowsWithoutTelegramCalls()
    {
        var chat = Chat(101, "Пост общий");
        var wrongPeer = ResultRow(Chat(202, "Пост общий"));
        var called = false;

        var finished = await new ChatReviewAnalysisCoordinator().RunAsync([chat], [wrongPeer],
            (_, _) =>
            {
                called = true;
                return Task.FromResult((Recognized(), 1, 2));
            }, () => true);

        Assert.False(finished);
        Assert.False(called);
        Assert.Equal("Pending", wrongPeer.StatusKind);
    }

    [Fact]
    public async Task ChangedPeerType_RejectsResultsEvenWithSameIdAndTitle()
    {
        var chat = Chat(101);
        var wrongType = new ChatAnalysisResultRow(chat.Title, chat.PeerId, "Chat");

        var finished = await new ChatReviewAnalysisCoordinator().RunAsync([chat], [wrongType],
            (_, _) => Task.FromResult((Recognized(), 1, 3)), () => true);

        Assert.False(finished);
        Assert.Equal("Pending", wrongType.StatusKind);
    }

    [Fact]
    public async Task InsufficientData_RemainsWarning_AndCannotBeConnected()
    {
        var chat = Chat(101);
        var row = ResultRow(chat);
        var result = new ChatActivityAnalysis(ChatActivityClassification.INSUFFICIENT_DATA,
            null, 0, 0, []);

        var finished = await new ChatReviewAnalysisCoordinator().RunAsync([chat], [row],
            (_, _) => Task.FromResult((result, 7, 0)), () => true);

        Assert.True(finished);
        Assert.Equal("Warning", row.StatusKind);
        Assert.False(row.CanConnect);
    }

    [Fact]
    public async Task EmptySelection_DoesNotInvokeTelegram()
    {
        var finished = await new ChatReviewAnalysisCoordinator().RunAsync([], [],
            (_, _) => throw new InvalidOperationException("Must not run"), () => true);

        Assert.False(finished);
    }

    private static TelegramChatOption Chat(long id, string? title = null)
        => new(id, "Channel", title ?? $"Пост чат {id}", "Канал");

    private static ChatAnalysisResultRow ResultRow(TelegramChatOption chat)
        => new(chat.Title, chat.PeerId, chat.PeerType);

    private static ChatActivityAnalysis Recognized()
        => new(ChatActivityClassification.POSSIBLE_ACTIVITY,
            ChatTaskNetwork.INSTAGRAM, 1, 0, []);
}
