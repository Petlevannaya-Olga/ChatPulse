using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceMixedFollowStoreTests
{
    [Fact]
    public async Task MixedConfirmation_StoresBothDirectionsAndHidesOnlyAfterAllConfirmed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpulse-mixed-{Guid.NewGuid():N}.sqlite");
        var chat = new ConfiguredChat { TelegramUserId = 125, PeerType = "Chat", PeerId = 456 };
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ImportedChatMessages (
                        TelegramUserId INTEGER, PeerType TEXT, PeerId INTEGER, MessageId INTEGER);
                    CREATE TABLE ImportedMessageReactionStates (
                        TelegramUserId INTEGER, PeerType TEXT, PeerId INTEGER,
                        MessageId INTEGER, HasOwnReaction INTEGER);
                    INSERT INTO ImportedChatMessages VALUES (125, 'Chat', 456, 987);
                    INSERT INTO ImportedMessageReactionStates VALUES (125, 'Chat', 456, 987, 1);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var actions = new[]
            {
                new WorkspaceFollowAction(WorkspaceTaskKind.SUBSCRIBE, "joiner"),
                new WorkspaceFollowAction(WorkspaceTaskKind.UNSUBSCRIBE, "leaver")
            };
            var incomplete = await InstagramFollowTaskRules.VerifyActionsAsync("owner", actions,
                (name, _, _) => Task.FromResult(name == "leaver"
                    ? InstagramTaskCheckResult.Inconclusive("Подписка всё ещё есть")
                    : InstagramTaskCheckResult.Confirmed("Подтверждено")));
            Assert.False(incomplete.CanHide);
            var store = new WorkspaceSubscriptionTaskVerificationStore(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync(chat, 987, incomplete));

            var confirmed = await InstagramFollowTaskRules.VerifyActionsAsync("owner", actions,
                (_, _, _) => Task.FromResult(InstagramTaskCheckResult.Confirmed("Подтверждено")));
            Assert.True(confirmed.CanHide);
            await store.PublishAsync(chat, 987, confirmed);
            await using var checkConnection = new SqliteConnection($"Data Source={path}");
            await checkConnection.OpenAsync();
            using var check = checkConnection.CreateCommand();
            check.CommandText = """
                SELECT ActionKind || ':' || TargetUserName
                FROM ImportedMessageMixedFollowVerifiedActions
                WHERE MessageId = 987 ORDER BY ActionKind, TargetUserName
                """;
            var saved = new List<string>();
            await using (var reader = await check.ExecuteReaderAsync())
                while (await reader.ReadAsync()) saved.Add(reader.GetString(0));
            Assert.Equal(new[] { "SUBSCRIBE:joiner", "UNSUBSCRIBE:leaver" }, saved);
            check.CommandText = "SELECT COUNT(*) FROM ImportedMessageHidden WHERE MessageId=987";
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
        }
        finally { SqliteTestDatabaseCleanup.DeleteFile(path); }
    }
}
