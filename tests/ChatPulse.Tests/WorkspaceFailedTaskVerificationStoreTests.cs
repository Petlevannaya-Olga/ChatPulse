using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceFailedTaskVerificationStoreTests
{
    [Fact]
    public void ReportPreservesSelectedChecksAndTheirDifferentOutcomes()
    {
        var result = new InstagramTaskVerificationResult("soul_light_workshop",
            "https://www.instagram.com/p/test/",
            InstagramTaskCheckResult.Confirmed("Лайк есть"),
            InstagramTaskCheckResult.Inconclusive("Комментарий не загружен"));

        var report = WorkspaceFailedTaskReport.FromPost(result);

        Assert.True(report.HasFailure);
        Assert.Collection(report.Checks,
            check => { Assert.Equal("Лайк", check.Name); Assert.True(check.Confirmed); },
            check => { Assert.Equal("Комментарий", check.Name); Assert.False(check.Confirmed); });
    }

    [Fact]
    public async Task FailedAttemptsAreAppendOnlyAndNeverHideMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-failure-{Guid.NewGuid():N}.sqlite");
        try
        {
            var chat = new ConfiguredChat { TelegramUserId = 125, PeerType = "Chat", PeerId = 456 };
            await using (var db = new SqliteConnection($"Data Source={path}"))
            {
                await db.OpenAsync();
                using var create = db.CreateCommand();
                create.CommandText = """
                    CREATE TABLE ImportedChatMessages (TelegramUserId INTEGER, PeerType TEXT,
                        PeerId INTEGER, MessageId INTEGER);
                    CREATE TABLE ImportedMessageReactionStates (TelegramUserId INTEGER,
                        PeerType TEXT, PeerId INTEGER, MessageId INTEGER, HasOwnReaction INTEGER);
                    INSERT INTO ImportedChatMessages VALUES (125, 'Chat', 456, 987);
                    INSERT INTO ImportedMessageReactionStates VALUES (125, 'Chat', 456, 987, 1);
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var store = new WorkspaceFailedTaskVerificationStore(path);
            var first = WorkspaceFailedTaskReport.FromError("Публикация", "soul_light_workshop",
                "https://instagram.com/p/example/", ["Лайк"], "Сеть недоступна");
            var second = WorkspaceFailedTaskReport.FromError("Публикация", "soul_light_workshop",
                "https://instagram.com/p/example/", ["Комментарий"], "Не удалось загрузить список");
            await store.SaveAsync(chat, 987, first);
            await store.SaveAsync(chat, 987, second);

            var reports = await store.ReadAsync(chat, 987);
            Assert.Equal(2, reports.Count);
            Assert.Equal("Комментарий", reports[0].Checks.Single().Name);
            Assert.Equal("Лайк", reports[1].Checks.Single().Name);
            Assert.Contains(987, await store.FindWithReportsAsync(chat, [987, 986]));

            await using var verification = new SqliteConnection($"Data Source={path}");
            await verification.OpenAsync();
            using var command = verification.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='ImportedMessageHidden'";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
