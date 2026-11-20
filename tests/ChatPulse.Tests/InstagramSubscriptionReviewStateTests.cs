using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramSubscriptionReviewStateTests
{
    private static readonly IReadOnlySet<string> EmptyOwnNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task AddedParticipantRequiresReviewUntilCompletedBulkCheckAndSurvivesRestart()
    {
        var path = TempPath();
        try
        {
            var chat = CreateChat(1);
            Insert(path, chat, "alice", "alice", "Обычный");
            var review = new InstagramSubscriptionReviewStateStore(path);
            var snapshot = await review.CaptureAsync([chat], EmptyOwnNames);
            CompleteBatch(path, "main", "check1", snapshot);
            Assert.False(await review.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));

            Insert(path, chat, "bob", "bob", "Обычный");
            var reloaded = new InstagramSubscriptionReviewStateStore(path);
            Assert.True(await reloaded.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));

            // An individual check is not a full scan and cannot clear the warning.
            var individual = new InstagramBatchRunStore(path);
            individual.Stage("one", "main", "checker", Progress("bob", "following", 1, 1));
            individual.PublishIndividual("one", "main", "bob", InstagramCheckDirections.FOLLOWING);
            Assert.True(await reloaded.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));

            var currentSnapshot = await review.CaptureAsync([chat], EmptyOwnNames);
            CompleteBatch(path, "main", "check2", currentSnapshot);
            Assert.False(await reloaded.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public async Task ReorderedRowsChangedWeekendAndVipActivityDoNotRequireReview()
    {
        var path = TempPath();
        try
        {
            var chat = CreateChat(2);
            Insert(path, chat, "one", "one.activity", "VIP");
            Insert(path, chat, "two", "two.activity", "Обычный");
            var state = new InstagramSubscriptionReviewStateStore(path);
            CompleteBatch(path, "main", "first", await state.CaptureAsync([chat], EmptyOwnNames));
            Execute(path, """
                UPDATE ChatParticipants
                SET Position=Position+1, Status='WEEKEND'
                WHERE PeerId=2;
                UPDATE ChatParticipants
                SET ActivityProfileUrl='https://www.instagram.com/new.vip.activity'
                WHERE PeerId=2 AND SubscriptionProfileUrl LIKE '%/one';
                """);
            Assert.False(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));

            // A category change is relevant only if it changes check eligibility.
            Execute(path, """
                UPDATE ChatParticipants SET Category='Обычный'
                WHERE PeerId=2 AND SubscriptionProfileUrl LIKE '%/one';
                """);
            Assert.True(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public async Task PartialBulkCheckDoesNotClearChangeInOtherDirection()
    {
        var path = TempPath();
        try
        {
            var chat = CreateChat(3);
            Insert(path, chat, "alice", "alice.active", "Обычный");
            var state = new InstagramSubscriptionReviewStateStore(path);
            CompleteBatch(path, "main", "first", await state.CaptureAsync([chat], EmptyOwnNames));
            Execute(path, """
                UPDATE ChatParticipants SET ActivityProfileUrl='https://www.instagram.com/new.activity'
                WHERE PeerId=3;
                """);
            Assert.True(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));

            CompleteBatch(path, "main", "following", await state.CaptureAsync([chat], EmptyOwnNames),
                InstagramCheckDirections.FOLLOWING);
            Assert.True(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));
            CompleteBatch(path, "main", "followers", await state.CaptureAsync([chat], EmptyOwnNames),
                InstagramCheckDirections.FOLLOWERS);
            Assert.False(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public async Task FailedBulkCheckAndUnchangedRosterCannotClearOrCreateWarning()
    {
        var path = TempPath();
        try
        {
            var chat = CreateChat(4);
            Insert(path, chat, "alice", "alice", "Обычный");
            var state = new InstagramSubscriptionReviewStateStore(path);
            var baseline = await state.CaptureAsync([chat], EmptyOwnNames);
            CompleteBatch(path, "main", "first", baseline);
            Assert.False(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));
            Insert(path, chat, "bob", "bob", "Обычный");
            var current = await state.CaptureAsync([chat], EmptyOwnNames);
            var store = new InstagramBatchRunStore(path);
            store.Stage("failed", "main", "checker", Progress("alice", "followers", 1, 1));
            Assert.Throws<InvalidOperationException>(() => store.PublishCompleted("failed", "main",
                Summary(2, 2, InstagramCheckDirections.BOTH), current));
            Assert.True(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));

            // Restoring exactly the checked roster removes the discrepancy.
            Execute(path, "UPDATE ChatParticipants SET IsCurrent=0 WHERE PeerId=4 AND SubscriptionProfileUrl LIKE '%/bob'");
            Assert.False(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public async Task BaselinesAreScopedToChatAndReceivingAccount()
    {
        var path = TempPath();
        try
        {
            var first = CreateChat(5);
            var second = CreateChat(6);
            Insert(path, first, "alice", "alice", "Обычный");
            Insert(path, second, "other", "other", "Обычный");
            var state = new InstagramSubscriptionReviewStateStore(path);
            CompleteBatch(path, "main", "first", await state.CaptureAsync([first, second], EmptyOwnNames));
            Insert(path, first, "bob", "bob", "Обычный");
            Assert.True(await state.NeedsReviewAsync("main", first, [first, second], EmptyOwnNames));
            Assert.False(await state.NeedsReviewAsync("main", second, [first, second], EmptyOwnNames));
            Assert.False(await state.NeedsReviewAsync("another.account", first, [first, second], EmptyOwnNames));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public async Task LegacyDatabaseWithoutBaselineRemembersFirstChangedRosterAndClearsOnlyOnBulkCheck()
    {
        var path = TempPath();
        try
        {
            var chat = CreateChat(7);
            Insert(path, chat, "alice", "alice", "Обычный");
            var state = new InstagramSubscriptionReviewStateStore(path);
            var original = await state.CaptureAsync([chat], EmptyOwnNames);
            Insert(path, chat, "new", "new", "Обычный");
            var changed = await state.CaptureAsync([chat], EmptyOwnNames);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                InstagramSubscriptionReviewStateStore.RecordChanges(connection, transaction,
                    "main", original, changed);
                transaction.Commit();
            }
            Assert.True(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));
            CompleteBatch(path, "main", "new", changed);
            Assert.False(await state.NeedsReviewAsync("main", chat, [chat], EmptyOwnNames));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(),
        $"ChatPulseReview_{Guid.NewGuid():N}.db");

    private static ConfiguredChat CreateChat(long peerId) => new()
    {
        TelegramUserId = 900, PeerId = peerId, PeerType = "Channel", SocialNetwork = "Instagram",
        SynchronizationState = ChatSynchronizationState.COMPLETED
    };

    private static void Insert(string path, ConfiguredChat chat, string subscription,
        string activity, string category)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS ChatParticipants (
                    ParticipantId INTEGER PRIMARY KEY AUTOINCREMENT,
                    TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                    Position INTEGER, DisplayName TEXT, SubscriptionProfileUrl TEXT NOT NULL,
                    ActivityProfileUrl TEXT NOT NULL, Category TEXT NOT NULL,
                    Status TEXT NOT NULL, IsCurrent INTEGER NOT NULL, SourceMessageId INTEGER NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ChatParticipants
                (TelegramUserId, PeerType, PeerId, Position, DisplayName, SubscriptionProfileUrl,
                 ActivityProfileUrl, Category, Status, IsCurrent, SourceMessageId)
            VALUES ($owner, $type, $peer, 1, NULL, $sub, $activity, $category, 'ACTIVE', 1, 1)
            """;
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$type", chat.PeerType);
        command.Parameters.AddWithValue("$peer", chat.PeerId);
        command.Parameters.AddWithValue("$sub", "https://www.instagram.com/" + subscription);
        command.Parameters.AddWithValue("$activity", "https://www.instagram.com/" + activity);
        command.Parameters.AddWithValue("$category", category);
        command.ExecuteNonQuery();
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CompleteBatch(string path, string target, string runId,
        IReadOnlyList<InstagramReviewFingerprint> baseline,
        InstagramCheckDirections directions = InstagramCheckDirections.BOTH)
    {
        var store = new InstagramBatchRunStore(path);
        var kinds = new List<string>();
        if ((directions & InstagramCheckDirections.FOLLOWERS) != 0) kinds.Add("followers");
        if ((directions & InstagramCheckDirections.FOLLOWING) != 0) kinds.Add("following");
        for (var index = 0; index < kinds.Count; index++)
            store.Stage(runId, target, "checker", Progress("member", kinds[index],
                index + 1, kinds.Count));
        store.PublishCompleted(runId, target, Summary(kinds.Count, kinds.Count, directions), baseline);
    }

    private static InstagramBatchProgress Progress(string name, string kind, int completed, int total)
        => new(name, kind, new InstagramSearchResult(InstagramSearchStatus.FOUND, "ok"),
            completed, total);

    private static InstagramBatchResultSummary Summary(int completed, int total,
        InstagramCheckDirections directions)
        => new(DateTimeOffset.UtcNow, InstagramBatchOutcome.COMPLETED,
            completed, total, completed, 0, 0, 0, 0,
            NotFoundFollowers: 0, NotFoundFollowing: 0, Directions: directions);
}
