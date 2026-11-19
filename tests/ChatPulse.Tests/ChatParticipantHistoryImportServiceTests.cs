using System.IO;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatParticipantHistoryImportServiceTests
{
    private const string LIST = """
        🌟VIP УЧАСТНИКИ🌟
        1 VIP
        https://instagram.com/vip_user
        ⚡️🦋 ВСЕ МЫ 🦋⚡️
        1
        https://instagram.com/anna
        2
        https://instagram.com/masha
        3
        https://instagram.com/alex
        """;

    private static ConfiguredChat Chat() => new()
    {
        TelegramUserId = 54321, PeerType = "Channel", PeerId = 14,
        TelegramTitle = "14 ЧАТ"
    };

    [Fact]
    public async Task ImportProcessesEverySnapshot_InOrder_AndPreservesFailedMessageAndReentry()
    {
        using var temp = new TemporaryDatabase();
        var reader = new StubReader([
            new TelegramChatMessage(40, LIST),
            new TelegramChatMessage(10, LIST),
            new TelegramChatMessage(20, LIST.Replace("https://instagram.com/anna",
                "https://www.com/anna", StringComparison.Ordinal)),
            new TelegramChatMessage(30, LIST.Replace("https://instagram.com/anna",
                "https://instagram.com/bob", StringComparison.Ordinal))
        ]);
        var history = new ChatParticipantHistoryStore(temp.Path);
        var service = new ChatParticipantHistoryImportService(reader, history,
            new Chat14ParticipantHistoryParser());

        var result = await service.RebuildAsync(Chat());

        Assert.Equal("PARTIAL", result.Outcome);
        Assert.Equal(3, result.AcceptedCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.Equal(new long[] { 10, 20, 30, 40 },
            (await history.ReadSnapshotsAsync(Chat())).Select(item => item.MessageId));
        var anna = (await history.ReadChangesAsync(Chat()))
            .Where(item => item.InstagramUsername == "anna").ToArray();
        Assert.Equal(new long[] { 10, 30, 40 }, anna.Select(item => item.MessageId));
        Assert.Equal(new bool[] { true, false, true }, anna.Select(item => item.IsPresent));
        Assert.Contains(await history.ReadSnapshotsAsync(Chat()), item =>
            item.MessageId == 20 && item.ParseStatus == "REJECTED" &&
            item.Error!.Contains("www.com", StringComparison.Ordinal));
        Assert.Equal(4, (await history.ReadCurrentAsync(Chat())).Count);
        Assert.Equal(1, reader.ReadCount);

        await service.RebuildAsync(Chat());
        var annaAgain = (await history.ReadChangesAsync(Chat()))
            .Where(item => item.InstagramUsername == "anna").ToArray();
        Assert.Equal(anna.Select(item => (item.MessageId, item.IsPresent)),
            annaAgain.Select(item => (item.MessageId, item.IsPresent)));
    }

    [Fact]
    public async Task MergesWeekendAndPenaltyWithoutChangingSubscriptionAccountOrCreatingDuplicateIdentity()
    {
        using var temp = new TemporaryDatabase();
        var history = new ChatParticipantHistoryStore(temp.Path);
        var list = LIST.Replace("https://instagram.com/anna",
            "https://instagram.com/anna\nВыходные с 22 по 29 октября", StringComparison.Ordinal)
            + "\n❌ШТРАФНЫЕ ДНИ❌\n4\nhttps://www.com/annaХ/";
        var service = new ChatParticipantHistoryImportService(
            new StubReader([new TelegramChatMessage(15, list,
                SentAtUtc: new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc))]),
            history, new Chat14ParticipantHistoryParser());

        var result = await service.RebuildAsync(Chat());

        Assert.Equal("COMPLETED", result.Outcome);
        var current = await history.ReadCurrentAsync(Chat());
        var anna = Assert.Single(current.Where(item => item.InstagramUsername == "anna"));
        Assert.True(anna.IsWeekend);
        Assert.True(anna.IsPenalty);
        Assert.Equal(new DateOnly(2026, 10, 22), anna.WeekendFromDate);
        Assert.Equal(new DateOnly(2026, 10, 29), anna.WeekendToDate);
        Assert.Equal(4, current.Count);
    }

    [Fact]
    public async Task NoMatchingListDoesNotDeletePreviouslyRestoredHistory()
    {
        using var temp = new TemporaryDatabase();
        var history = new ChatParticipantHistoryStore(temp.Path);
        var original = new ParticipantHistoryCandidate(1, null, "source",
        [
            new ParticipantHistoryEntry("https://www.instagram.com/keep",
                "https://www.instagram.com/keep", "Обычный")
        ]);
        await history.RebuildAsync(Chat(), [original], new Chat14ParticipantHistoryParser().Rule);
        var service = new ChatParticipantHistoryImportService(new StubReader([]), history,
            new Chat14ParticipantHistoryParser());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RebuildAsync(Chat()));
        Assert.Equal("keep", Assert.Single(await history.ReadCurrentAsync(Chat())).InstagramUsername);
    }

    [Fact]
    public async Task AllListsRejectedDoNotOverwriteEarlierHistory()
    {
        using var temp = new TemporaryDatabase();
        var history = new ChatParticipantHistoryStore(temp.Path);
        await history.RebuildAsync(Chat(), [new ParticipantHistoryCandidate(1, null, "source",
        [
            new ParticipantHistoryEntry("https://www.instagram.com/keep",
                "https://www.instagram.com/keep", "Обычный")
        ])], new Chat14ParticipantHistoryParser().Rule);
        var bad = LIST.Replace("https://instagram.com/masha", "https://www.com/masha",
            StringComparison.Ordinal);
        var service = new ChatParticipantHistoryImportService(
            new StubReader([new TelegramChatMessage(5, bad)]), history,
            new Chat14ParticipantHistoryParser());

        var result = await service.RebuildAsync(Chat());
        Assert.Equal("FAILED", result.Outcome);
        Assert.Equal("keep", Assert.Single(await history.ReadCurrentAsync(Chat())).InstagramUsername);
        var report = await history.ReadLastRunAsync(Chat());
        Assert.Equal("FAILED", report!.Value.Outcome);
        Assert.Contains("www.com", report.Value.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsParsingProgressAndSavingAfterAllListsAreProcessed()
    {
        using var temp = new TemporaryDatabase();
        var history = new ChatParticipantHistoryStore(temp.Path);
        var service = new ChatParticipantHistoryImportService(
            new StubReader([new TelegramChatMessage(11, LIST),
                new TelegramChatMessage(12, LIST)]), history,
            new Chat14ParticipantHistoryParser());
        var progress = new List<(int Processed, int Total)>();
        var savingStarted = false;

        var result = await service.RebuildAsync(Chat(),
            onCandidateProcessed: (processed, total) => progress.Add((processed, total)),
            onSaving: () => savingStarted = true);

        Assert.Equal("COMPLETED", result.Outcome);
        Assert.Equal(new[] { (1, 2), (2, 2) }, progress);
        Assert.True(savingStarted);
    }

    private sealed class StubReader(IReadOnlyList<TelegramChatMessage> messages) :
        ITelegramParticipantListHistoryReader
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<TelegramChatMessage>> FindAllMatchingMessagesAsync(
            TelegramChatOption chat, Func<string?, bool> looksLikeParticipantList,
            CancellationToken cancellationToken, Action<int, int, int>? onPageScanned = null)
        {
            ReadCount++;
            var matching = messages.Where(item => looksLikeParticipantList(item.Text)).ToArray();
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>(matching);
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "chatpulse-import-" + Guid.NewGuid().ToString("N"));
        public TemporaryDatabase() => Directory.CreateDirectory(_directory);
        public string Path => System.IO.Path.Combine(_directory, "history.db");
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
