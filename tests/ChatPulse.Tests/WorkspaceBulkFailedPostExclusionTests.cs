using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceBulkFailedPostExclusionTests
{
    [Theory]
    [InlineData(WorkspaceTaskKind.TASK, true, true)]
    [InlineData(WorkspaceTaskKind.TASK, false, false)]
    [InlineData(WorkspaceTaskKind.SUBSCRIBE, true, false)]
    [InlineData(WorkspaceTaskKind.UNSUBSCRIBE, true, false)]
    public void OnlyPreviouslyFailedPostsAreExcluded(
        WorkspaceTaskKind kind, bool hasFailedReport, bool expected)
    {
        Assert.Equal(expected,
            WorkspaceBulkTaskVerificationPolicy.ShouldExcludeFailedPost(kind, hasFailedReport));
    }

    [Fact]
    public async Task Snapshot_ExcludesPostsWithReportsAcrossPages_ButIncludesFailedFollowTasks()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chatpulse-bulk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "history.db");
        try
        {
            var chat = new ConfiguredChat
            {
                TelegramUserId = 123,
                PeerType = "Channel",
                PeerId = 456,
                TelegramTitle = "Постчат",
                DisplayName = "Постчат",
                SocialNetwork = "Instagram",
                SynchronizationState = ChatSynchronizationState.COMPLETED,
                HistoryDays = 7
            };
            await SeedAsync(path);
            var reports = new WorkspaceFailedTaskVerificationStore(path);
            foreach (var id in new long[] { 1, 61, 65 })
                await reports.SaveAsync(chat, id, WorkspaceFailedTaskReport.FromError(
                    "Публикация", "soul_light_workshop", $"https://www.instagram.com/p/test{id}/",
                    ["Лайк"], "Ранее обнаружена ошибка"));
            await reports.SaveAsync(chat, 64, WorkspaceFailedTaskReport.FromError(
                "Подписка", "soul_light_workshop", "profile_test", ["Подписка"],
                "Ранее обнаружена ошибка"));

            using var browser = new InstagramBrowserService(Path.Combine(directory, "chrome"));
            var runner = new WorkspaceBulkTaskVerifier(new WorkspaceTaskReader(path, pooling: false), browser);
            var snapshot = await runner.GetSnapshotAsync(chat, oldestFirst: true, () => false);

            Assert.Equal(62, snapshot.Count);
            Assert.Equal(3, runner.ExcludedFailedPostCount);
            Assert.DoesNotContain(1L, snapshot);
            Assert.DoesNotContain(61L, snapshot);
            Assert.DoesNotContain(65L, snapshot);
            Assert.Contains(64L, snapshot); // Subscriptions are not excluded.
            Assert.Contains(60L, snapshot); // Last item of the first unfiltered page.
            Assert.Contains(63L, snapshot); // Next page was not lost after filtering.

            // Even an ID captured before a new failure report must not be rechecked.
            var skipped = await runner.VerifyOneAsync(chat, 61, "soul_light_workshop");
            Assert.True(skipped.Skipped);
            Assert.False(skipped.Hidden);
            Assert.Contains("вручную", skipped.Detail);
        }
        finally
        {
            SqliteTestDatabaseCleanup.DeleteDirectory(directory);
        }
    }

    private static async Task SeedAsync(string path)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await db.OpenAsync();
        using var schema = db.CreateCommand();
        schema.CommandText = """
            CREATE TABLE ImportedChatMessages (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL, Text TEXT NOT NULL);
            CREATE TABLE ImportedMessageReactionStates (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL, HasOwnReaction INTEGER NOT NULL);
            """;
        await schema.ExecuteNonQueryAsync();
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO ImportedChatMessages VALUES (123, 'Channel', 456, $id, $text);
            INSERT INTO ImportedMessageReactionStates VALUES (123, 'Channel', 456, $id, 1);
            """;
        var idParameter = insert.Parameters.Add("$id", SqliteType.Integer);
        var textParameter = insert.Parameters.Add("$text", SqliteType.Text);
        for (var id = 1; id <= 65; id++)
        {
            idParameter.Value = id;
            textParameter.Value = id == 64
                ? "Подписка https://www.instagram.com/profile_test/"
                : $"Лайк https://www.instagram.com/p/test{id}/";
            await insert.ExecuteNonQueryAsync();
        }
    }
}
