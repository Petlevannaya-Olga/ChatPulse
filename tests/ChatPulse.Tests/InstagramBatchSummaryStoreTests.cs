using System.IO;
using ChatPulse.Desktop.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramBatchSummaryStoreTests
{
    [Fact]
    public void MissingDatabaseHasNoPreviousBatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-summary-{Guid.NewGuid():N}.db");
        Assert.Null(new InstagramBatchSummaryStore(path).Read("account"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void OnlyCompletedRunCanReplacePublishedSummary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-summary-{Guid.NewGuid():N}.db");
        try
        {
            var store = new InstagramBatchSummaryStore(path);
            var completed = new InstagramBatchResultSummary(
                new DateTimeOffset(2026, 11, 18, 12, 20, 0, TimeSpan.Zero),
                InstagramBatchOutcome.COMPLETED, 81, 81, 71, 10, 0, 17, 0,
                NotFoundFollowers: 3, NotFoundFollowing: 7);
            var interrupted = new InstagramBatchResultSummary(
                new DateTimeOffset(2026, 11, 18, 13, 0, 0, TimeSpan.Zero),
                InstagramBatchOutcome.STOPPED, 6, 81, 4, 1, 1, 17, 0,
                "Проверка остановлена пользователем.");
            store.Save("@main_account", completed);
            store.Save("other_account", completed);

            Assert.Throws<ArgumentException>(() => store.Save("MAIN_ACCOUNT", interrupted));
            Assert.Equal(completed, new InstagramBatchSummaryStore(path).Read("main_account"));
            Assert.Equal(completed, store.Read("OTHER_ACCOUNT"));
            Assert.Equal(7, store.Read("main_account")!.NotFoundFollowing!.Value);
            Assert.Equal(3, store.Read("main_account")!.NotFoundFollowers!.Value);
            Assert.False(completed.HasDetails);
            Assert.True(interrupted.IsInterrupted);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ErrorSummaryIsNotPublishedAndLegacyErrorSummaryIsNotShown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-summary-error-{Guid.NewGuid():N}.db");
        try
        {
            var store = new InstagramBatchSummaryStore(path);
            var successful = new InstagramBatchResultSummary(
                DateTimeOffset.UtcNow, InstagramBatchOutcome.COMPLETED, 2, 2,
                1, 1, 0, 0, 0, NotFoundFollowers: 1, NotFoundFollowing: 0);
            store.Save("main", successful);
            var invalid = successful with { Found = 0, Errors = 1 };
            Assert.Throws<ArgumentException>(() => store.Save("main", invalid));
            Assert.Equal(successful, store.Read("main"));

            // Simulate a faulty summary previously saved by an older app version.
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE InstagramBatchSummaries
                    SET Found=0, Errors=1
                    WHERE TargetUserName='main'
                    """;
                command.ExecuteNonQuery();
            }
            Assert.Null(store.Read("main"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReadsOldSummaryWithoutDirectionalColumnsAndMigratesItOnSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-summary-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE InstagramBatchSummaries (
                        TargetUserName TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                        FinishedAtUtc TEXT NOT NULL,
                        Outcome TEXT NOT NULL,
                        CompletedSearches INTEGER NOT NULL,
                        TotalSearches INTEGER NOT NULL,
                        Found INTEGER NOT NULL,
                        NotFound INTEGER NOT NULL,
                        Errors INTEGER NOT NULL,
                        PrivilegedParticipants INTEGER NOT NULL,
                        MissingActivityProfiles INTEGER NOT NULL,
                        Details TEXT NOT NULL);
                    INSERT INTO InstagramBatchSummaries VALUES
                        ('main', '2026-11-18T12:20:00+00:00', 'COMPLETED', 10, 10, 8, 2, 0, 0, 0, '');
                    """;
                command.ExecuteNonQuery();
            }
            var store = new InstagramBatchSummaryStore(path);
            var previous = store.Read("main");
            Assert.NotNull(previous);
            Assert.True(previous.HasLegacyBreakdown);
            var newer = previous with { NotFoundFollowers = 1, NotFoundFollowing = 1 };
            store.Save("main", newer);
            Assert.True(store.Read("main")!.HasNotFoundBreakdown);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SearchNotFoundIsNotPresentedAsConfirmedUnfollow()
    {
        var summary = new InstagramBatchResultSummary(
            DateTimeOffset.UtcNow, InstagramBatchOutcome.COMPLETED, 10, 10, 7, 3, 0, 2, 0,
            NotFoundFollowers: 1, NotFoundFollowing: 2);
        Assert.Contains("не доказывает отписку", summary.Caution);
        Assert.Equal(3, summary.NotFoundFollowers.GetValueOrDefault() + summary.NotFoundFollowing.GetValueOrDefault());
        Assert.True(summary.HasNotFoundBreakdown);
    }
}
