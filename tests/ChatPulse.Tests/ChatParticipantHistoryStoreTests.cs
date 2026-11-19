using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatParticipantHistoryStoreTests
{
    private static ConfiguredChat Chat(long peer = 14) => new()
    {
        TelegramUserId = 123,
        PeerId = peer,
        PeerType = "Channel",
        TelegramTitle = "14 чат"
    };

    private static ParticipantHistoryEntry Entry(string username,
        string category = "Обычный", bool weekend = false, bool penalty = false,
        string? activity = null, string section = "ВСЕ МЫ") => new(
        $"https://www.instagram.com/{username}",
        $"https://www.instagram.com/{activity ?? username}",
        category, weekend, penalty, SourceSection: section);

    private static ParticipantHistoryCandidate Candidate(long id, params ParticipantHistoryEntry[] entries)
        => new(id, new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc),
            $"СПИСОК УЧАСТНИЦ {id}", entries);

    private static ParticipantHistoryRule Rule() => new(1, "Chat14Rules", "{}");

    [Fact]
    public async Task RebuildProcessesEveryVersionInMessageOrder_AndPreservesTransitions()
    {
        using var directory = new TempDirectory();
        var store = new ChatParticipantHistoryStore(directory.Database);
        var duplicate = Entry("anna", category: "VIP");
        var messages = new[]
        {
            Candidate(30, Entry("anna", "VIP", weekend: true, penalty: true), Entry("third")),
            Candidate(10, duplicate, duplicate, Entry("second")),
            new ParticipantHistoryCandidate(25, null, "повреждённый список", null,
                "Строка 7: недопустимый домен www.com"),
            Candidate(20, Entry("second"), Entry("third"))
        };

        var result = await store.RebuildAsync(Chat(), messages, Rule());

        Assert.Equal("PARTIAL", result.Outcome);
        Assert.Equal(4, result.FoundCount);
        Assert.Equal(3, result.AcceptedCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.Equal(10L, result.OldestMessageId);
        Assert.Equal(30L, result.NewestMessageId);
        Assert.Equal(new long[] { 10, 20, 25, 30 },
            (await store.ReadSnapshotsAsync(Chat())).Select(item => item.MessageId).ToArray());
        Assert.Contains(await store.ReadSnapshotsAsync(Chat()), item =>
            item.MessageId == 25 && item.ParseStatus == "REJECTED" &&
            item.Error!.Contains("www.com", StringComparison.Ordinal));

        var states = await store.ReadChangesAsync(Chat());
        Assert.Equal(new long[] { 10, 20, 30 },
            states.Where(item => item.InstagramUsername == "anna")
                .Select(item => item.MessageId).ToArray());
        Assert.Equal(new bool[] { true, false, true },
            states.Where(item => item.InstagramUsername == "anna")
                .Select(item => item.IsPresent).ToArray());
        Assert.DoesNotContain(states, item => item.MessageId == 25);
        var current = await store.ReadCurrentAsync(Chat());
        Assert.Equal(2, current.Count);
        var anna = Assert.Single(current.Where(item => item.InstagramUsername == "anna"));
        Assert.True(anna.IsWeekend);
        Assert.True(anna.IsPenalty);

        // Exact duplicates are preserved as source occurrences, but not identities.
        await using var connection = new SqliteConnection($"Data Source={directory.Database};Pooling=False");
        await connection.OpenAsync();
        var raw = connection.CreateCommand();
        raw.CommandText = "SELECT COUNT(*) FROM ChatParticipantListEntries WHERE SnapshotId = " +
                          "(SELECT SnapshotId FROM ChatParticipantListSnapshots WHERE SourceMessageId=10)";
        Assert.Equal(3L, Convert.ToInt64(await raw.ExecuteScalarAsync()));
        var unique = connection.CreateCommand();
        unique.CommandText = "SELECT COUNT(*) FROM ChatParticipantHistoryIdentities";
        Assert.Equal(3L, Convert.ToInt64(await unique.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RebuildIsIdempotent_AndReplacesEditedMessageWithoutAffectingOtherChatsOrLiveRegister()
    {
        using var directory = new TempDirectory();
        // Existing live subscriptions data must survive historical rebuilds.
        await using (var existing = new SqliteConnection($"Data Source={directory.Database};Pooling=False"))
        {
            await existing.OpenAsync();
            using var setup = existing.CreateCommand();
            setup.CommandText = "CREATE TABLE ChatParticipants (Value TEXT); INSERT INTO ChatParticipants VALUES ('keep');";
            await setup.ExecuteNonQueryAsync();
        }
        var store = new ChatParticipantHistoryStore(directory.Database);
        var first = new[] { Candidate(10, Entry("first")), Candidate(11, Entry("second")) };
        var otherChat = Chat(15);
        await store.RebuildAsync(otherChat, [Candidate(10, Entry("other"))], Rule());
        await store.RebuildAsync(Chat(), first, Rule());
        var expected = (await store.ReadChangesAsync(Chat()))
            .Select(item => (item.InstagramUsername, item.MessageId, item.IsPresent)).ToArray();
        await store.RebuildAsync(Chat(), first, Rule());
        Assert.Equal(expected, (await store.ReadChangesAsync(Chat()))
            .Select(item => (item.InstagramUsername, item.MessageId, item.IsPresent)).ToArray());
        Assert.Single(await store.ReadCurrentAsync(otherChat));

        await store.RebuildAsync(Chat(), [Candidate(10, Entry("first")),
            Candidate(11, Entry("third"))], Rule());
        Assert.Equal(new[] { "third" },
            (await store.ReadCurrentAsync(Chat())).Select(item => item.InstagramUsername).ToArray());
        Assert.Equal("other", Assert.Single(await store.ReadCurrentAsync(otherChat)).InstagramUsername);

        await using var connection = new SqliteConnection($"Data Source={directory.Database};Pooling=False");
        await connection.OpenAsync();
        var live = connection.CreateCommand();
        live.CommandText = "SELECT Value FROM ChatParticipants";
        Assert.Equal("keep", await live.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RebuildKeepsPriorHistoryWhenRuleVersionIsReusedForDifferentSettings()
    {
        using var directory = new TempDirectory();
        var store = new ChatParticipantHistoryStore(directory.Database);
        await store.RebuildAsync(Chat(), [Candidate(10, Entry("first"))], Rule());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RebuildAsync(Chat(),
            [Candidate(11, Entry("second"))], new ParticipantHistoryRule(1, "DifferentParser")));

        Assert.Equal("first", Assert.Single(await store.ReadCurrentAsync(Chat())).InstagramUsername);
        Assert.Single(await store.ReadSnapshotsAsync(Chat()));
    }

    [Fact]
    public async Task IncompleteOrConflictingListsAreRejectedWithoutFalseDepartures()
    {
        using var directory = new TempDirectory();
        var store = new ChatParticipantHistoryStore(directory.Database);
        var result = await store.RebuildAsync(Chat(),
        [
            Candidate(10, Entry("stays")),
            Candidate(11, new ParticipantHistoryEntry("https://www.com/stays",
                "https://www.instagram.com/stays", "Обычный")),
            Candidate(12, Entry("stays"), Entry("stays", activity: "another"))
        ], Rule());

        Assert.Equal("PARTIAL", result.Outcome);
        Assert.Equal(2, result.RejectedCount);
        Assert.Single(await store.ReadCurrentAsync(Chat()));
        var states = await store.ReadChangesAsync(Chat());
        Assert.Single(states);
        Assert.Equal(10L, states[0].MessageId);
        Assert.All((await store.ReadSnapshotsAsync(Chat())).Where(item => item.MessageId > 10),
            item => Assert.Equal("REJECTED", item.ParseStatus));
    }

    [Fact]
    public async Task WeekendAndPenaltySectionsMayBothReferToVipParticipant()
    {
        using var directory = new TempDirectory();
        var store = new ChatParticipantHistoryStore(directory.Database);
        var result = await store.RebuildAsync(Chat(),
        [
            Candidate(100,
                Entry("vip_user", "VIP", activity: "vip_activity", section: "VIP"),
                Entry("vip_user", "Обычный", weekend: true, section: "ВЫХОДНЫЕ"),
                Entry("vip_user", "Обычный", penalty: true, section: "ШТРАФНЫЕ ДНИ"))
        ], Rule());

        Assert.Equal("COMPLETED", result.Outcome);
        var state = Assert.Single(await store.ReadCurrentAsync(Chat()));
        Assert.Equal("VIP", state.Category);
        Assert.True(state.IsWeekend);
        Assert.True(state.IsPenalty);
        Assert.Equal("https://www.instagram.com/vip_activity", state.ActivityProfileUrl);
    }

    [Fact]
    public async Task EmptyOrDuplicateCandidateSetDoesNotOverwriteHistory()
    {
        using var directory = new TempDirectory();
        var store = new ChatParticipantHistoryStore(directory.Database);
        await store.RebuildAsync(Chat(), [Candidate(10, Entry("first"))], Rule());
        await Assert.ThrowsAsync<ArgumentException>(() => store.RebuildAsync(Chat(), [], Rule()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RebuildAsync(Chat(),
            [Candidate(10, Entry("a")), Candidate(10, Entry("b"))], Rule()));
        Assert.Equal("first", Assert.Single(await store.ReadCurrentAsync(Chat())).InstagramUsername);
    }

    [Fact]
    public async Task AllRejectedListsKeepPreviousHistoryAndSaveFailedRun()
    {
        using var directory = new TempDirectory();
        var store = new ChatParticipantHistoryStore(directory.Database);
        await store.RebuildAsync(Chat(), [Candidate(10, Entry("retained"))], Rule());

        var failure = await store.RebuildAsync(Chat(),
            [new ParticipantHistoryCandidate(20, null, "ошибка", null, "Недопустимый домен")], Rule());

        Assert.Equal("FAILED", failure.Outcome);
        Assert.Equal("retained", Assert.Single(await store.ReadCurrentAsync(Chat())).InstagramUsername);
        Assert.Single(await store.ReadSnapshotsAsync(Chat()));
        var run = await store.ReadLastRunAsync(Chat());
        Assert.NotNull(run);
        Assert.Equal("FAILED", run.Value.Outcome);
        Assert.Contains("Недопустимый домен", run.Value.Errors);
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
            $"chatpulse-history-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(_path);
        public string Database => Path.Combine(_path, "history.db");

        public void Dispose()
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }
}
