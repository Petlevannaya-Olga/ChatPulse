using System.IO;
using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramBatchRunStoreTests
{
    [Fact]
    public void InterruptedBatchDoesNotOverwriteLastSuccessfulChecksOrSummary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-batch-{Guid.NewGuid():N}.db");
        try
        {
            var resultStore = new InstagramSubscriptionCheckStore(path);
            resultStore.Save("main", "checker", Progress("member", "following", InstagramSearchStatus.FOUND, 1, 2));
            var previous = Summary(2, 2, 2, 0, InstagramBatchOutcome.COMPLETED);
            var summaryStore = new InstagramBatchSummaryStore(path);
            summaryStore.Save("main", previous);

            var store = new InstagramBatchRunStore(path);
            const string runId = "stopped_run";
            store.Stage(runId, "main", "checker", Progress("member", "following", InstagramSearchStatus.NOT_FOUND, 1, 2));
            store.RecordInterrupted(runId, "main", Summary(1, 2, 0, 1, InstagramBatchOutcome.STOPPED));
            Assert.Equal(0, CountRows(path, "InstagramBatchCheckDrafts"));

            var current = resultStore.Read("main", ["member"]);
            Assert.Single(current);
            Assert.Equal("FOUND", current[0].Status);
            Assert.Equal(previous, summaryStore.Read("main"));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public void CompletedBatchPublishesBothDirectionsAndLastSuccessfulSummary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-batch-{Guid.NewGuid():N}.db");
        try
        {
            var resultStore = new InstagramSubscriptionCheckStore(path);
            resultStore.Save("main", "checker", Progress("old.member", "followers", InstagramSearchStatus.FOUND, 1, 1));
            var store = new InstagramBatchRunStore(path);
            const string runId = "completed_run";
            store.Stage(runId, "main", "checker", Progress("member", "followers", InstagramSearchStatus.NOT_FOUND, 1, 2));
            store.Stage(runId, "main", "checker", Progress("member", "following", InstagramSearchStatus.FOUND, 2, 2));
            var summary = Summary(2, 2, 1, 1, InstagramBatchOutcome.COMPLETED);
            store.PublishCompleted(runId, "main", summary);

            Assert.Empty(resultStore.Read("main", ["old.member"]));
            var checks = resultStore.Read("main", ["member"]);
            Assert.Equal(2, checks.Count);
            Assert.Contains(checks, check => check.Kind == "followers" && check.Status == "NOT_FOUND");
            Assert.Contains(checks, check => check.Kind == "following" && check.Status == "FOUND");
            Assert.Equal(summary, new InstagramBatchSummaryStore(path).Read("main"));
            Assert.Equal(0, CountRows(path, "InstagramBatchCheckDrafts"));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public void IncompleteDraftCannotPublishAndLeavesOldDataUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-batch-{Guid.NewGuid():N}.db");
        try
        {
            var resultStore = new InstagramSubscriptionCheckStore(path);
            resultStore.Save("main", "checker", Progress("member", "following", InstagramSearchStatus.FOUND, 1, 2));
            var previous = Summary(2, 2, 2, 0, InstagramBatchOutcome.COMPLETED);
            new InstagramBatchSummaryStore(path).Save("main", previous);
            var store = new InstagramBatchRunStore(path);
            store.Stage("run", "main", "checker", Progress("member", "followers", InstagramSearchStatus.NOT_FOUND, 1, 2));
            Assert.Throws<InvalidOperationException>(() => store.PublishCompleted("run", "main",
                Summary(2, 2, 1, 1, InstagramBatchOutcome.COMPLETED)));
            Assert.Equal("FOUND", Assert.Single(resultStore.Read("main", ["member"])).Status);
            Assert.Equal(previous, new InstagramBatchSummaryStore(path).Read("main"));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public void FollowingOnlyBatchPreservesPreviouslyCheckedFollowers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-directions-{Guid.NewGuid():N}.db");
        try
        {
            var checks = new InstagramSubscriptionCheckStore(path);
            checks.Save("main", "checker", Progress("member", "followers", InstagramSearchStatus.FOUND, 1, 1));
            checks.Save("main", "checker", Progress("member", "following", InstagramSearchStatus.FOUND, 1, 1));
            var store = new InstagramBatchRunStore(path);
            store.Stage("following_only", "main", "checker",
                Progress("member", "following", InstagramSearchStatus.NOT_FOUND, 1, 1));
            var summary = new InstagramBatchResultSummary(DateTimeOffset.UtcNow,
                InstagramBatchOutcome.COMPLETED, 1, 1, 0, 1, 0, 0, 0,
                NotFoundFollowers: 0, NotFoundFollowing: 1,
                Directions: InstagramCheckDirections.FOLLOWING);
            store.PublishCompleted("following_only", "main", summary);
            var latest = checks.Read("main", ["member"]);
            Assert.Contains(latest, x => x.Kind == "followers" && x.Status == "FOUND");
            Assert.Contains(latest, x => x.Kind == "following" && x.Status == "NOT_FOUND");
            var savedSummary = new InstagramBatchSummaryStore(path).Read("main");
            Assert.NotNull(savedSummary);
            Assert.Equal(InstagramCheckDirections.FOLLOWING, savedSummary.Directions);
            Assert.Equal("—", savedSummary.NotFoundFollowersLabel);
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public void CompletedWithOneInconclusiveSearchCannotReplaceLastSuccessfulRun()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-error-{Guid.NewGuid():N}.db");
        try
        {
            var checks = new InstagramSubscriptionCheckStore(path);
            checks.Save("main", "checker", Progress("member", "following", InstagramSearchStatus.FOUND, 1, 1));
            var previous = Summary(1, 1, 1, 0, InstagramBatchOutcome.COMPLETED);
            var summaryStore = new InstagramBatchSummaryStore(path);
            summaryStore.Save("main", previous);

            var store = new InstagramBatchRunStore(path);
            store.Stage("error_run", "main", "checker",
                Progress("member", "following", InstagramSearchStatus.INCONCLUSIVE, 1, 1));
            var invalid = previous with { Found = 0, Errors = 1 };
            Assert.Throws<InvalidOperationException>(() => store.PublishCompleted("error_run", "main", invalid));
            store.RecordInterrupted("error_run", "main", invalid with { Outcome = InstagramBatchOutcome.FAILED });

            Assert.Equal("FOUND", Assert.Single(checks.Read("main", ["member"])).Status);
            Assert.Equal(previous, summaryStore.Read("main"));
            Assert.Equal(0, CountRows(path, "InstagramBatchCheckDrafts"));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public void InconclusiveIndividualSearchDoesNotReplacePreviousResult()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-individual-error-{Guid.NewGuid():N}.db");
        try
        {
            var checks = new InstagramSubscriptionCheckStore(path);
            checks.Save("main", "checker", Progress("member", "following", InstagramSearchStatus.FOUND, 1, 1));
            var store = new InstagramBatchRunStore(path);
            store.Stage("individual_error", "main", "checker",
                Progress("member", "following", InstagramSearchStatus.INCONCLUSIVE, 1, 1));
            Assert.Throws<InvalidOperationException>(() => store.PublishIndividual("individual_error", "main",
                "member", InstagramCheckDirections.FOLLOWING));
            Assert.Equal("FOUND", Assert.Single(checks.Read("main", ["member"])).Status);
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public void PreviouslySavedInconclusiveStatusesAreNotDisplayed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-legacy-error-{Guid.NewGuid():N}.db");
        try
        {
            var checks = new InstagramSubscriptionCheckStore(path);
            checks.Save("main", "checker", Progress("member", "followers", InstagramSearchStatus.INCONCLUSIVE, 1, 2));
            checks.Save("main", "checker", Progress("member", "following", InstagramSearchStatus.FOUND, 2, 2));
            var visible = checks.Read("main", ["member"]);
            Assert.Single(visible);
            Assert.Equal("following", visible[0].Kind);
            Assert.Equal("FOUND", visible[0].Status);
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public void IndividualCheckUpdatesOnlyOneDirectionAndLeavesBatchSummaryUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-individual-{Guid.NewGuid():N}.db");
        try
        {
            var checks = new InstagramSubscriptionCheckStore(path);
            checks.Save("main", "checker", Progress("member", "followers", InstagramSearchStatus.FOUND, 1, 1));
            checks.Save("main", "checker", Progress("member", "following", InstagramSearchStatus.NOT_FOUND, 1, 1));
            var summary = Summary(2, 2, 2, 0, InstagramBatchOutcome.COMPLETED);
            new InstagramBatchSummaryStore(path).Save("main", summary);
            var store = new InstagramBatchRunStore(path);
            store.Stage("individual", "main", "checker",
                Progress("member", "following", InstagramSearchStatus.FOUND, 1, 1));
            store.PublishIndividual("individual", "main", "member", InstagramCheckDirections.FOLLOWING);
            var latest = checks.Read("main", ["member"]);
            Assert.Contains(latest, x => x.Kind == "followers" && x.Status == "FOUND");
            Assert.Contains(latest, x => x.Kind == "following" && x.Status == "FOUND");
            Assert.Equal(summary, new InstagramBatchSummaryStore(path).Read("main"));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public void IndividualWithoutDraftCannotReplacePreviousStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-individual-{Guid.NewGuid():N}.db");
        try
        {
            var checks = new InstagramSubscriptionCheckStore(path);
            checks.Save("main", "checker", Progress("member", "followers", InstagramSearchStatus.FOUND, 1, 1));
            Assert.Throws<InvalidOperationException>(() => new InstagramBatchRunStore(path)
                .PublishIndividual("missing", "main", "member", InstagramCheckDirections.FOLLOWERS));
            Assert.Equal("FOUND", Assert.Single(checks.Read("main", ["member"])).Status);
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    [Fact]
    public void IndividualTwoDifferentAccountNamesArePublishedTogether()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-direction-{Guid.NewGuid():N}.db");
        try
        {
            var store = new InstagramBatchRunStore(path);
            store.Stage("pair", "main", "checker", Progress("member.active", "followers",
                InstagramSearchStatus.FOUND, 1, 2));
            store.Stage("pair", "main", "checker", Progress("member.subscribe", "following",
                InstagramSearchStatus.NOT_FOUND, 2, 2));
            store.PublishIndividualDirections("pair", "main", "member.active", "member.subscribe",
                InstagramCheckDirections.BOTH);
            var checks = new InstagramSubscriptionCheckStore(path).Read("main",
                ["member.active", "member.subscribe"]);
            Assert.Contains(checks, x => x.UserName == "member.active" && x.Kind == "followers");
            Assert.Contains(checks, x => x.UserName == "member.subscribe" && x.Kind == "following");
            Assert.Equal(0, CountRows(path, "InstagramBatchCheckDrafts"));
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }

    private static long CountRows(string path, string table)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static InstagramBatchProgress Progress(string name, string kind, InstagramSearchStatus status,
        int completed, int total) => new(name, kind, new InstagramSearchResult(status, "test"), completed, total);

    private static InstagramBatchResultSummary Summary(int completed, int total, int found, int notFound,
        InstagramBatchOutcome outcome) => new(DateTimeOffset.UtcNow, outcome, completed, total,
            found, notFound, 0, 0, 0, NotFoundFollowers: notFound, NotFoundFollowing: 0);
}
