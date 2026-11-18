using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ParticipantRegistryRefreshTests
{
    private const string ORIGINAL_REGISTER = """
        СПИСОК УЧАСТНИЦ
        1
        https://www.instagram.com/first_check
        2
        https://www.instagram.com/second_check
        3
        https://www.instagram.com/third_check
        """;

    private const string UPDATED_REGISTER = """
        СПИСОК УЧАСТНИЦ
        1
        https://www.instagram.com/first_check
        2
        https://www.instagram.com/second_check
        3
        https://www.instagram.com/fourth_check
        """;

    [Fact]
    public async Task RefreshLatestParticipantList_AppliesNewestValidListWithoutReloadingTasks()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        Assert.Equal(1, await registry.UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]));

        var telegram = Client();
        var searched = false;
        telegram.LatestMatchingMessageImpl = (_, matches, _) =>
        {
            searched = true;
            // The remote reader examines newer messages before older ones.
            var messages = new TelegramChatMessage[]
            {
                new(24, "Актив https://www.instagram.com/p/task123/"),
                new(23, UPDATED_REGISTER),
                new(10, ORIGINAL_REGISTER)
            };
            return Task.FromResult<TelegramChatMessage?>(messages.FirstOrDefault(
                item => matches(item.Text)));
        };

        var beforeCursor = (await settings.LoadAsync()).Chats.Single().LastProcessedMessageId;
        var sync = new ChatSynchronizationService(settings, telegram, database);
        var refreshed = await sync.RefreshLatestParticipantListAsync(chat);

        Assert.True(searched);
        Assert.Equal(23L, refreshed.SourceMessageId);
        Assert.True(refreshed.Applied);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "third_check"));
        Assert.Equal(beforeCursor, (await settings.LoadAsync()).Chats.Single().LastProcessedMessageId);
        Assert.Null(telegram.NewMessagesImpl);
    }

    [Fact]
    public async Task RefreshLatestParticipantList_ReportsActualSearchProgressAndSaveStage()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var telegram = Client();
        telegram.ReportLatestMatchingPage = report => report(2, 130);
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(23, UPDATED_REGISTER));
        var progress = new List<ParticipantListSyncProgress>();

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat, progress.Add);

        Assert.True(result.Applied);
        Assert.Equal(23L, result.SourceMessageId);
        Assert.Equal(ParticipantListSyncStage.SETTINGS, progress[0].Stage);
        Assert.Equal(ParticipantListSyncStage.TELEGRAM, progress[1].Stage);
        Assert.Contains(progress, item => item.Stage == ParticipantListSyncStage.SEARCH &&
            item.PagesScanned == 2 && item.MessagesScanned == 130);
        Assert.Equal(ParticipantListSyncStage.SAVE, progress[^1].Stage);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
    }

    [Fact]
    public async Task RefreshLatestParticipantList_CancelledSearchDoesNotReplaceCurrentRegister()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]));
        var telegram = Client();
        using var cancellation = new CancellationTokenSource();
        telegram.LatestMatchingMessageImpl = (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<TelegramChatMessage?>(new(23, UPDATED_REGISTER));
        };
        var progress = new List<ParticipantListSyncProgress>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ChatSynchronizationService(settings, telegram, database)
                .RefreshLatestParticipantListAsync(chat, progress.Add, cancellation.Token));

        Assert.DoesNotContain(progress, item => item.Stage == ParticipantListSyncStage.SAVE);
        Assert.Equal(1L, await CountCurrentAsync(database, "third_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "fourth_check"));
    }

    [Fact]
    public async Task NormalSynchronization_PicksNewestRegisterEvenIfRecentHistoryContainsOlderOne()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]));
        var telegram = Client();
        telegram.NewMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(20, UPDATED_REGISTER)]);
        telegram.ChatMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(10, ORIGINAL_REGISTER)]);

        await new ChatSynchronizationService(settings, telegram, database)
            .SynchronizeLatestAsync(chat);

        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "third_check"));
    }

    [Fact]
    public async Task RefreshLatestParticipantList_WhenNoListFoundPreservesExistingRegister()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]));
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, matches, _) =>
        {
            Assert.False(matches("Актив https://www.instagram.com/p/abc123/"));
            return Task.FromResult<TelegramChatMessage?>(null);
        };

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Null(result.SourceMessageId);
        Assert.False(result.Applied);
        Assert.Equal(1L, await CountCurrentAsync(database, "third_check"));
    }

    [Fact]
    public async Task RefreshLatestParticipantList_OlderListCannotOverwriteCurrent()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(30, UPDATED_REGISTER)]));
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(10, ORIGINAL_REGISTER));

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Equal(10L, result.SourceMessageId);
        Assert.False(result.Applied);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "third_check"));
    }

    [Fact]
    public async Task RefreshLatestParticipantList_EditedMessageWithSameIdUpdatesRegister()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(30, ORIGINAL_REGISTER)]));
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(30, UPDATED_REGISTER));

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Equal(30L, result.SourceMessageId);
        Assert.True(result.Applied);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "third_check"));
    }

    private static async Task<long> CountCurrentAsync(string database, string username)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM ChatParticipants WHERE TelegramUserId = 123
                AND PeerType = 'Channel' AND PeerId = 101 AND IsCurrent = 1
                AND ActivityProfileUrl = $profile
            """;
        command.Parameters.AddWithValue("$profile", $"https://www.instagram.com/{username}");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static ConfiguredChat Chat() => new()
    {
        TelegramUserId = 123,
        PeerType = "Channel",
        PeerId = 101,
        TelegramTitle = "Пост чат 101",
        SocialNetwork = "Instagram",
        SynchronizationState = ChatSynchronizationState.COMPLETED,
        HistoryDays = 7
    };

    private static FakeTelegramClient Client() => new()
    {
        CurrentTelegramUserId = 123,
        ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
            [new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа")]))
    };

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-RegistrySync-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
