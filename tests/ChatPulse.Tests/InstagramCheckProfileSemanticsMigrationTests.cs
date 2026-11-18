using ChatPulse.Desktop.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramCheckProfileSemanticsMigrationTests
{
    [Fact]
    public void InvalidatesOldFollowingResultsAndSummaryOnceButKeepsValidFollowers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-semantics-{Guid.NewGuid():N}.db");
        try
        {
            var checks = new InstagramSubscriptionCheckStore(path);
            checks.Save("main", "viewer", Progress("member.active", "followers"));
            checks.Save("main", "viewer", Progress("member.active", "following"));
            var summary = new InstagramBatchResultSummary(DateTimeOffset.UtcNow,
                InstagramBatchOutcome.COMPLETED, 2, 2, 2, 0, 0, 0, 0,
                NotFoundFollowers: 0, NotFoundFollowing: 0);
            new InstagramBatchSummaryStore(path).Save("main", summary);
            var runStore = new InstagramBatchRunStore(path);
            runStore.Stage("abandoned", "main", "viewer", Progress("member.active", "following"));

            var migration = new InstagramCheckProfileSemanticsMigration(path);
            migration.EnsureApplied();
            var found = checks.Read("main", ["member.active"]);
            var item = Assert.Single(found);
            Assert.Equal("followers", item.Kind);
            Assert.Null(new InstagramBatchSummaryStore(path).Read("main"));
            Assert.Equal(0, Count(path, "InstagramBatchCheckDrafts"));

            checks.Save("main", "viewer", Progress("member.subscribe", "following"));
            migration.EnsureApplied(); // must not invalidate a new-format result on subsequent starts
            Assert.Contains(checks.Read("main", ["member.subscribe"]), x => x.Kind == "following");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static InstagramBatchProgress Progress(string user, string kind) =>
        new(user, kind, new InstagramSearchResult(InstagramSearchStatus.FOUND, "Найден"), 1, 1);

    private static long Count(string path, string table)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
