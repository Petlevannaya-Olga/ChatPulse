using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceMessageDatesTests
{
    private const string TASK_TEXT = "Актив https://www.instagram.com/p/example/";

    [Fact]
    public async Task InitialImport_PersistsOriginalTelegramDate()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = CreateChat();
        await settings.SaveChatAsync(chat);
        var sentAt = new DateTime(2026, 11, 16, 14, 35, 0, DateTimeKind.Utc);
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new TelegramChatMessage(10, TASK_TEXT, SentAtUtc: sentAt)]);
        var database = Path.Combine(temp.Path, "history.db");
        await new ChatSynchronizationService(settings, client, database).SynchronizeAsync(chat);
        var task = Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat));
        Assert.Equal(new DateTimeOffset(sentAt), task.SentAtUtc);
    }

    [Fact]
    public async Task Refresh_PopulatesOldDatesWithoutChangingMessageTextOrCompletion()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = CreateChat();
        await settings.SaveChatAsync(chat);
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new TelegramChatMessage(10, TASK_TEXT, true)]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        await sync.SynchronizeAsync(chat);
        var before = Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat));
        Assert.Null(before.SentAtUtc);
        var sentAt = new DateTime(2026, 11, 15, 18, 21, 0, DateTimeKind.Utc);
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new TelegramChatMessage(10, "Изменённый текст", null, SentAtUtc: sentAt)]);
        await sync.RefreshReactionsAsync(chat);
        var after = Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat));
        Assert.Equal(new DateTimeOffset(sentAt), after.SentAtUtc);
        Assert.Equal(TASK_TEXT, after.Text);
        Assert.True(after.IsCompleted);
    }

    private static ConfiguredChat CreateChat() => new()
    {
        TelegramUserId = 123, PeerId = 101, PeerType = "Channel",
        TelegramTitle = "Пост чат 101", DisplayName = "Пост чат 101",
        SocialNetwork = "Instagram", HistoryDays = 7
    };

    private static FakeTelegramClient CreateClient() => new()
    {
        CurrentTelegramUserId = 123,
        ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
            [new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа")]))
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Dates-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
