using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceBulkFailedPostExclusionTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void OnlyManualReviewFailuresAreExcluded(bool requiresManualReview, bool expected)
    {
        Assert.Equal(expected,
            WorkspaceBulkTaskVerificationPolicy.ShouldExcludeFromBulk(requiresManualReview));
    }

    [Theory]
    [InlineData(true, WorkspaceBulkVerificationMode.ExcludePreviouslyChecked, true)]
    [InlineData(false, WorkspaceBulkVerificationMode.ExcludePreviouslyChecked, false)]
    [InlineData(true, WorkspaceBulkVerificationMode.CheckAll, false)]
    [InlineData(false, WorkspaceBulkVerificationMode.CheckAll, false)]
    public void ModeDeterminesManualExclusion(bool hasManualReviewReport,
        WorkspaceBulkVerificationMode mode, bool expected)
    {
        Assert.Equal(expected, WorkspaceBulkTaskVerificationPolicy.ShouldExcludeFromBulk(
            hasManualReviewReport, mode));
    }

    [Fact]
    public async Task Snapshot_ExcludesUnconfirmedPostsAndFollows_AcrossPages_ButRetriesTechnicalFailures()
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
                "На странице отображается «Подписаться»: подписка не подтверждена."));
            await reports.SaveAsync(chat, 63, WorkspaceFailedTaskReport.FromError(
                "Отписка", "soul_light_workshop", "profile_test", ["Отписка"],
                "На странице отображается «Подписки»: отписка не подтверждена."));
            // Already saved technical reports must NOT prevent another bulk attempt.
            // An old report has no FailureCategory property at all.
            var legacyCrash = WorkspaceFailedTaskReport.FromError("Публикация",
                "soul_light_workshop", "https://www.instagram.com/p/test2/", ["Репост"],
                "Техническая ошибка: session not created: Chrome failed to start: crashed")
                with { FailureCategory = null };
            await reports.SaveAsync(chat, 2, legacyCrash);
            await reports.SaveAsync(chat, 62, WorkspaceFailedTaskReport.FromError(
                "Подписка", "soul_light_workshop", "profile_test", ["Подписка"],
                "Проверка не завершена: invalid session id", technicalFailure: true));
            await reports.SaveAsync(chat, 60, WorkspaceFailedTaskReport.FromError(
                "Отписка", "soul_light_workshop", "profile_test", ["Отписка"],
                "Ошибка Chrome при проверке отписки", technicalFailure: true));

            var manual = await reports.FindRequiringManualReviewAsync(chat,
                [1L, 2L, 60L, 61L, 62L, 63L, 64L, 65L]);
            Assert.Equal(new long[] { 1, 61, 63, 64, 65 }, manual.OrderBy(id => id));
            Assert.Contains(2L, await reports.FindWithReportsAsync(chat, [2L]));

            using var browser = new InstagramBrowserService(Path.Combine(directory, "chrome"));
            var runner = new WorkspaceBulkTaskVerifier(new WorkspaceTaskReader(path, pooling: false), browser);
            var snapshot = await runner.GetSnapshotAsync(chat, oldestFirst: true, () => false);

            Assert.Equal(60, snapshot.Count);
            Assert.Equal(3, runner.ExcludedFailedPostCount);
            Assert.Equal(2, runner.ExcludedFailedFollowCount);
            Assert.DoesNotContain(1L, snapshot);
            Assert.DoesNotContain(61L, snapshot);
            Assert.DoesNotContain(65L, snapshot);
            Assert.DoesNotContain(63L, snapshot); // Unconfirmed unsubscription -> manual.
            Assert.DoesNotContain(64L, snapshot); // Unconfirmed subscription -> manual.
            Assert.Contains(2L, snapshot); // Legacy Chrome startup failure -> automatic retry.
            Assert.Contains(60L, snapshot); // Technical unsubscription failure -> retry.
            Assert.Contains(62L, snapshot); // Technical subscription failure -> retry.
            Assert.Contains(60L, snapshot); // Last item of the first unfiltered page.
            Assert.Contains(62L, snapshot); // Next page was not lost after filtering.

            // Explicit full mode includes every still-visible completed task,
            // including all five tasks with prior business failures.
            var all = await runner.GetSnapshotAsync(chat, oldestFirst: true, () => false,
                WorkspaceBulkVerificationMode.CheckAll);
            Assert.Equal(65, all.Count);
            Assert.Equal(0, runner.ExcludedFailedPostCount);
            Assert.Equal(0, runner.ExcludedFailedFollowCount);
            foreach (var id in new long[] { 1, 61, 63, 64, 65 })
                Assert.Contains(id, all);

            // Even an ID captured before a new failure report must not be rechecked.
            var skipped = await runner.VerifyOneAsync(chat, 61, "soul_light_workshop");
            Assert.True(skipped.Skipped);
            Assert.False(skipped.Hidden);
            Assert.Contains("вручную", skipped.Detail);
            var skippedFollow = await runner.VerifyOneAsync(chat, 64, "soul_light_workshop");
            Assert.True(skippedFollow.Skipped);
            Assert.False(skippedFollow.Hidden);

            // A manual-review report appearing AFTER the snapshot must still
            // block the task before opening Chrome.
            await reports.SaveAsync(chat, 62, WorkspaceFailedTaskReport.FromError(
                "Подписка", "soul_light_workshop", "profile_test", ["Подписка"],
                "Подписка не подтверждена."));
            var newlySkipped = await runner.VerifyOneAsync(chat, 62, "soul_light_workshop");
            Assert.True(newlySkipped.Skipped);
            Assert.False(newlySkipped.Hidden);

            // CheckAll skips the manual-review gate. This message has no profile
            // check result that can be verified in a unit test without Chrome, so
            // the explicit all-mode behavior is verified at the policy + snapshot
            // level, whereas the runtime uses the same mode for the per-item gate.
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
            textParameter.Value = id is 64 or 62
                ? "Подписка https://www.instagram.com/profile_test/"
                : id is 63 or 60
                    ? "Отписка https://www.instagram.com/profile_test/"
                    : $"Лайк https://www.instagram.com/p/test{id}/";
            await insert.ExecuteNonQueryAsync();
        }
    }
}
