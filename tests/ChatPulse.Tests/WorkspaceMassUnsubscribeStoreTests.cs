using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceMassUnsubscribeStoreTests
{
    [Fact]
    public async Task AllUnsubscribed_AreStoredAndHiddenInOneTransaction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-unsubscribe-{Guid.NewGuid():N}.sqlite");
        var chat = new ConfiguredChat { TelegramUserId = 125, PeerType = "Chat", PeerId = 456 };
        try
        {
            await InitializeAsync(path);
            var verification = await InstagramFollowTaskRules.VerifyTargetsAsync("owner",
                WorkspaceTaskKind.UNSUBSCRIBE, ["first", "second", "third"],
                (_, _, _) => Task.FromResult(InstagramTaskCheckResult.Confirmed("Нет подписки")));
            await new WorkspaceSubscriptionTaskVerificationStore(path).PublishAsync(chat, 987, verification);
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            using var check = connection.CreateCommand();
            check.CommandText = """
                SELECT TargetUserName FROM ImportedMessageSubscriptionVerifiedTargets
                WHERE MessageId=987 ORDER BY TargetUserName
                """;
            var targets = new List<string>();
            await using (var reader = await check.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) targets.Add(reader.GetString(0));
            }
            Assert.Equal(new[] { "first", "second", "third" }, targets);
            check.CommandText = "SELECT COUNT(*) FROM ImportedMessageHidden WHERE MessageId=987";
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteTestDatabaseCleanup.DeleteFile(path);
        }
    }

    [Fact]
    public async Task OneStillFollowing_DoesNotPublishAnyConfirmationOrHide()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-unsubscribe-{Guid.NewGuid():N}.sqlite");
        var chat = new ConfiguredChat { TelegramUserId = 125, PeerType = "Chat", PeerId = 456 };
        try
        {
            await InitializeAsync(path);
            var verification = await InstagramFollowTaskRules.VerifyTargetsAsync("owner",
                WorkspaceTaskKind.UNSUBSCRIBE, ["first", "second"],
                (name, _, _) => Task.FromResult(name == "second"
                    ? InstagramTaskCheckResult.Inconclusive("Подписка есть")
                    : InstagramTaskCheckResult.Confirmed("Нет подписки")));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new WorkspaceSubscriptionTaskVerificationStore(path).PublishAsync(chat, 987, verification));
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='ImportedMessageHidden'";
            Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteTestDatabaseCleanup.DeleteFile(path);
        }
    }

    private static async Task InitializeAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ImportedChatMessages (
                TelegramUserId INTEGER, PeerType TEXT, PeerId INTEGER, MessageId INTEGER);
            CREATE TABLE ImportedMessageReactionStates (
                TelegramUserId INTEGER, PeerType TEXT, PeerId INTEGER, MessageId INTEGER,
                HasOwnReaction INTEGER);
            INSERT INTO ImportedChatMessages VALUES (125, 'Chat', 456, 987);
            INSERT INTO ImportedMessageReactionStates VALUES (125, 'Chat', 456, 987, 1);
            """;
        await command.ExecuteNonQueryAsync();
    }
}
