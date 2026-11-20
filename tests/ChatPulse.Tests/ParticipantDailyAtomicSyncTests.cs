using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ParticipantDailyAtomicSyncTests
{
    private const string FIRST = """
        СПИСОК УЧАСТНИЦ
        1
        https://www.instagram.com/alpha_test
        2
        https://www.instagram.com/beta_test
        3
        https://www.instagram.com/gamma_test
        """;

    private const string SECOND = """
        СПИСОК УЧАСТНИЦ
        1
        https://www.instagram.com/alpha_test
        2
        https://www.instagram.com/beta_test
        3
        https://www.instagram.com/delta_test
        """;

    private const string THIRD = """
        СПИСОК УЧАСТНИЦ
        1
        https://www.instagram.com/alpha_test
        2
        https://www.instagram.com/epsilon_test
        3
        https://www.instagram.com/delta_test
        """;

    private const string BROKEN = """
        СПИСОК УЧАСТНИЦ
        1
        https://www.instagram.com/alpha_test
        2
        https://www.com/incorrect_domain
        3
        https://www.instagram.com/delta_test
        """;

    [Fact]
    public async Task MultipleNewListsAreAppliedInOrderToHistoryAndLiveRegister()
    {
        using var temp = new TestDirectory();
        var (chat, settings, database, telegram) = await PrepareAsync(temp, withPrevious: true);
        using (settings)
        {
            telegram.AllMatchingMessagesImpl = (_, matches, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                [new(30, THIRD), new(10, FIRST), new(20, SECOND)]
                    .Where(message => matches(message.Text)).ToArray());
            var result = await new ChatSynchronizationService(settings, telegram, database)
                .RefreshLatestParticipantListAsync(chat);

            Assert.Equal(ParticipantListApplyStatus.APPLIED, result.Status);
            Assert.Equal(2, result.ProcessedListCount);
            Assert.Equal(30L, (await new ChatParticipantRegistry(database)
                .ReadLatestSourceAsync(chat))?.MessageId);
            var history = new ChatParticipantHistoryStore(database);
            Assert.Equal(new long[] { 20, 30 },
                (await history.ReadSnapshotsAsync(chat)).Select(item => item.MessageId));
            Assert.Contains(await history.ReadChangesAsync(chat), item =>
                item.InstagramUsername == "beta_test" && item.MessageId == 30 && !item.IsPresent);
            Assert.Equal(1L, await CountCurrentAsync(database, "epsilon_test"));
            Assert.Equal(0L, await CountCurrentAsync(database, "beta_test"));
        }
    }

    [Fact]
    public async Task AppendsToPreviouslyRestoredFullHistoryWithoutRecreatingOldEvents()
    {
        using var temp = new TestDirectory();
        var (chat, settings, database, telegram) = await PrepareAsync(temp, withPrevious: true);
        using (settings)
        {
            var history = new ChatParticipantHistoryStore(database);
            var initial = new ParticipantHistoryCandidate(10, null, FIRST,
            [
                new ParticipantHistoryEntry("https://www.instagram.com/alpha_test",
                    "https://www.instagram.com/alpha_test", "Обычный"),
                new ParticipantHistoryEntry("https://www.instagram.com/beta_test",
                    "https://www.instagram.com/beta_test", "Обычный"),
                new ParticipantHistoryEntry("https://www.instagram.com/gamma_test",
                    "https://www.instagram.com/gamma_test", "Обычный")
            ]);
            var rule = new StandardParticipantHistoryParser(true).Rule;
            await history.RebuildAsync(chat, [initial], rule);
            telegram.AllMatchingMessagesImpl = (_, matches, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                [new TelegramChatMessage(20, SECOND)].Where(item => matches(item.Text)).ToArray());

            var result = await new ChatSynchronizationService(settings, telegram, database)
                .RefreshLatestParticipantListAsync(chat);

            Assert.True(result.Applied);
            Assert.Equal(new long[] { 10, 20 },
                (await history.ReadSnapshotsAsync(chat)).Select(item => item.MessageId));
            Assert.Contains(await history.ReadChangesAsync(chat), item =>
                item.InstagramUsername == "gamma_test" && item.MessageId == 20 && !item.IsPresent);
            Assert.Equal(20L, (await new ChatParticipantRegistry(database)
                .ReadLatestSourceAsync(chat))?.MessageId);
        }
    }

    [Fact]
    public async Task MalformedMiddleListAbortsEntireBatchAndLeavesCursorAndHistoryUntouched()
    {
        using var temp = new TestDirectory();
        var (chat, settings, database, telegram) = await PrepareAsync(temp, withPrevious: true);
        using (settings)
        {
            telegram.AllMatchingMessagesImpl = (_, matches, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                [new(30, THIRD), new(25, BROKEN), new(20, SECOND)]
                    .Where(message => matches(message.Text)).ToArray());
            var result = await new ChatSynchronizationService(settings, telegram, database)
                .RefreshLatestParticipantListAsync(chat);

            Assert.False(result.Applied);
            Assert.Equal(ParticipantListApplyStatus.UNPARSEABLE_MESSAGE, result.Status);
            Assert.Equal(25L, result.SourceMessageId);
            Assert.Contains("www.com", result.ParseFailureReason);
            Assert.Equal(10L, (await new ChatParticipantRegistry(database)
                .ReadLatestSourceAsync(chat))?.MessageId);
            Assert.Empty(await new ChatParticipantHistoryStore(database).ReadSnapshotsAsync(chat));
            Assert.Equal(1L, await CountCurrentAsync(database, "gamma_test"));
            Assert.Equal(0L, await CountCurrentAsync(database, "delta_test"));
            var report = await new ChatParticipantRegistry(database).ReadLastAttemptAsync(chat);
            Assert.Equal(25L, report?.MessageId);
            Assert.Contains("Предыдущий список сохранён", report?.Description);
        }
    }

    [Fact]
    public async Task FixedMiddleListCanBeReplayedWithoutDuplicateEvents()
    {
        using var temp = new TestDirectory();
        var (chat, settings, database, telegram) = await PrepareAsync(temp, withPrevious: true);
        using (settings)
        {
            var messages = new TelegramChatMessage[]
            {
                new(30, THIRD), new(25, BROKEN), new(20, SECOND)
            };
            telegram.AllMatchingMessagesImpl = (_, matches, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                messages.Where(item => matches(item.Text)).ToArray());
            var service = new ChatSynchronizationService(settings, telegram, database);
            Assert.False((await service.RefreshLatestParticipantListAsync(chat)).Applied);

            messages[1] = new TelegramChatMessage(25, SECOND);
            var accepted = await service.RefreshLatestParticipantListAsync(chat);
            Assert.Equal(3, accepted.ProcessedListCount);
            Assert.Equal(new long[] { 20, 25, 30 },
                (await new ChatParticipantHistoryStore(database).ReadSnapshotsAsync(chat))
                .Select(item => item.MessageId));

            var noChange = await service.RefreshLatestParticipantListAsync(chat);
            Assert.Equal(ParticipantListApplyStatus.ALREADY_CURRENT, noChange.Status);
            Assert.Equal(3, (await new ChatParticipantHistoryStore(database)
                .ReadSnapshotsAsync(chat)).Count);
        }
    }

    [Fact]
    public async Task SqliteFailureAfterHistoryChangesRollsBackAllNewSnapshotsAndLiveRows()
    {
        using var temp = new TestDirectory();
        var (chat, settings, database, telegram) = await PrepareAsync(temp, withPrevious: true);
        using (settings)
        {
            // Force an error AFTER the history writer has inserted new rows.
            // The current-register update then fails and must roll back history.
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER RejectLiveParticipantUpdate
                    BEFORE UPDATE ON ChatParticipants
                    WHEN NEW.IsCurrent=1
                    BEGIN
                        SELECT RAISE(ABORT, 'forced live update failure');
                    END;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            telegram.AllMatchingMessagesImpl = (_, matches, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                [new TelegramChatMessage(20, SECOND)].Where(item => matches(item.Text)).ToArray());
            await Assert.ThrowsAsync<SqliteException>(() =>
                new ChatSynchronizationService(settings, telegram, database)
                    .RefreshLatestParticipantListAsync(chat));

            Assert.Equal(10L, (await new ChatParticipantRegistry(database)
                .ReadLatestSourceAsync(chat))?.MessageId);
            Assert.Empty(await new ChatParticipantHistoryStore(database).ReadSnapshotsAsync(chat));
            Assert.Equal(0L, await CountCurrentAsync(database, "delta_test"));
        }
    }

    [Fact]
    public async Task TelegramReadFailureDoesNotChangeAnyDatabaseState()
    {
        using var temp = new TestDirectory();
        var (chat, settings, database, telegram) = await PrepareAsync(temp, withPrevious: true);
        using (settings)
        {
            telegram.AllMatchingMessagesImpl = (_, _, _) =>
                throw new IOException("Сетевая ошибка на промежуточной странице");
            await Assert.ThrowsAsync<IOException>(() =>
                new ChatSynchronizationService(settings, telegram, database)
                    .RefreshLatestParticipantListAsync(chat));
            Assert.Equal(10L, (await new ChatParticipantRegistry(database)
                .ReadLatestSourceAsync(chat))?.MessageId);
            Assert.Empty(await new ChatParticipantHistoryStore(database).ReadSnapshotsAsync(chat));
        }
    }

    private static async Task<(ConfiguredChat, JsonSettingsService, string, FakeTelegramClient)> PrepareAsync(
        TestDirectory directory, bool withPrevious)
    {
        var chat = new ConfiguredChat
        {
            TelegramUserId = 123, PeerId = 101, PeerType = "Channel",
            TelegramTitle = "Пост чат 101", SocialNetwork = "Instagram",
            SynchronizationState = ChatSynchronizationState.COMPLETED, HistoryDays = 7
        };
        var settings = new JsonSettingsService(directory.Path);
        await settings.SaveChatAsync(chat);
        var db = Path.Combine(directory.Path, "chatpulse.db");
        if (withPrevious)
            await new ChatParticipantRegistry(db).UpdateAsync(chat, [(10L, FIRST)]);
        var telegram = new FakeTelegramClient
        {
            CurrentTelegramUserId = 123,
            ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
                [new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа")]))
        };
        return (chat, settings, db, telegram);
    }

    private static async Task<long> CountCurrentAsync(string path, string username)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM ChatParticipants WHERE TelegramUserId=123 AND PeerId=101
              AND PeerType='Channel' AND IsCurrent=1 AND SubscriptionProfileUrl=$url
            """;
        command.Parameters.AddWithValue("$url", $"https://www.instagram.com/{username}");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulseAtomicSync_{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
