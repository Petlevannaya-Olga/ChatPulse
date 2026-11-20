using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Infrastructure.Telegram;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramParticipantHistoryTraversalTests
{
    [Fact]
    public async Task IncrementalTraversalKeepsEveryNewListButStopsAtCommittedCursor()
    {
        var offsets = new List<int>();
        var result = await TelegramParticipantHistoryTraversal.CollectAfterAsync(
            (offset, _) =>
            {
                offsets.Add(offset);
                IReadOnlyList<TelegramChatMessage> page = offset switch
                {
                    0 => [new(60, "LIST newest"), new(55, "task"), new(50, "LIST middle")],
                    50 => [new(45, "LIST oldest new"), new(40, "LIST already stored")],
                    _ => throw new InvalidOperationException("Cursor should stop traversal")
                };
                return Task.FromResult(page);
            },
            text => text?.StartsWith("LIST", StringComparison.Ordinal) == true,
            40, CancellationToken.None);

        Assert.Equal(new long[] { 45, 50, 60 }, result.Select(item => item.MessageId));
        Assert.Equal(new[] { 0, 50 }, offsets);
    }

    [Fact]
    public async Task IncrementalTraversalRejectsRepeatingPageWithoutReturningPartialData()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TelegramParticipantHistoryTraversal.CollectAfterAsync(
                (offset, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                    offset == 0 ? [new(40, "LIST")] : [new(40, "LIST")]),
                text => text == "LIST", 10, CancellationToken.None));
    }

    [Fact]
    public async Task ReadsAllAvailablePagesInPublicationOrder_AndKeepsOnlyCandidateLists()
    {
        var requestedOffsets = new List<int>();
        var progress = new List<(int Pages, int Scanned, int Found)>();
        var pages = new Dictionary<int, TelegramChatMessage[]>
        {
            [0] = [new(110, "task"), new(100, "LIST day3"), new(85, null)],
            [85] = [new(70, "LIST day2"), new(50, "task")],
            [50] = [new(20, "LIST day1"), new(10, "task")],
            [10] = []
        };
        var results = await TelegramParticipantHistoryTraversal.CollectAsync(
            (offset, _) =>
            {
                requestedOffsets.Add(offset);
                return Task.FromResult<IReadOnlyList<TelegramChatMessage>>(pages[offset]);
            },
            text => text?.StartsWith("LIST", StringComparison.Ordinal) == true,
            CancellationToken.None,
            (page, scanned, found) => progress.Add((page, scanned, found)));

        Assert.Equal(new long[] { 20, 70, 100 }, results.Select(item => item.MessageId).ToArray());
        Assert.Equal(new[] { 0, 85, 50, 10 }, requestedOffsets);
        Assert.Equal(new[] { (1, 3, 1), (2, 5, 2), (3, 7, 3) }, progress);
    }

    [Fact]
    public async Task DoesNotTrustShortPageAsEndOfAccessibleHistory()
    {
        var calls = 0;
        var messages = await TelegramParticipantHistoryTraversal.CollectAsync(
            (offset, _) =>
            {
                calls++;
                IReadOnlyList<TelegramChatMessage> page = offset switch
                {
                    0 => [new TelegramChatMessage(10, "LIST recent")],
                    10 => [new TelegramChatMessage(5, "LIST old")],
                    _ => []
                };
                return Task.FromResult(page);
            },
            text => text?.StartsWith("LIST", StringComparison.Ordinal) == true,
            CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal(new long[] { 5, 10 }, messages.Select(item => item.MessageId));
    }

    [Fact]
    public async Task TraversalCanGoBeyondFiveHundredPagesWithoutAHistoryDateCutoff()
    {
        var requests = 0;
        var matching = await TelegramParticipantHistoryTraversal.CollectAsync(
            (offset, _) =>
            {
                requests++;
                var nextId = offset == 0 ? 610 : offset - 1;
                IReadOnlyList<TelegramChatMessage> page = nextId <= 0 ? [] :
                    [new TelegramChatMessage(nextId,
                        nextId is 610 or 10 ? "LIST" : "task",
                        SentAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc))];
                return Task.FromResult(page);
            },
            text => text == "LIST", CancellationToken.None);

        Assert.Equal(611, requests);
        Assert.Equal(new long[] { 10, 610 }, matching.Select(item => item.MessageId));
    }

    [Fact]
    public async Task RepeatedOrAscendingPageIdsAreNotSilentlyAccepted()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TelegramParticipantHistoryTraversal.CollectAsync(
                (offset, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                    offset == 0 ? [new TelegramChatMessage(10, "LIST")]
                        : [new TelegramChatMessage(10, "LIST")]),
                _ => true, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsPagingWithoutReturningPartialHistory()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TelegramParticipantHistoryTraversal.CollectAsync(
                (offset, _) =>
                {
                    count++;
                    return Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                        [new TelegramChatMessage(offset == 0 ? 30 : 20, "LIST")]);
                },
                _ => true, cancellation.Token,
                (_, _, _) => cancellation.Cancel()));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UnexpectedNetworkErrorDoesNotProducePartialResult()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            TelegramParticipantHistoryTraversal.CollectAsync(
                (offset, _) => offset == 0
                    ? Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                        [new TelegramChatMessage(10, "LIST")])
                    : Task.FromException<IReadOnlyList<TelegramChatMessage>>(
                        new IOException("connection dropped")),
                _ => true, CancellationToken.None));
    }
}
