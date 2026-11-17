using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class IncrementalTaskSynchronizationTests
{
    private const string TASK = "https://www.instagram.com/p/abc123/";

    [Fact]
    public async Task SynchronizeLatest_StartsAfterLastOwnReactionAndImportsNewMessages()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat());
        var client = Client();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, true), new(11, TASK, false)]);
        var path = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, path);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);
        var cursorReceived = -1;
        client.NewMessagesImpl = (_, cursor, _) =>
        {
            cursorReceived = cursor;
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                [new(11, TASK, false), new(12, TASK, true)]);
        };
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, true), new(11, TASK, false), new(12, TASK, true)]);
        Assert.Equal(1, await sync.SynchronizeLatestAsync(chat));
        Assert.Equal(10, cursorReceived);
        var tasks = await new WorkspaceTaskReader(path).GetTasksAsync(chat);
        Assert.Equal(3, tasks.Count);
        Assert.True(tasks.Single(t => t.MessageId == 12).IsCompleted);
        Assert.False(tasks.Single(t => t.MessageId == 11).IsCompleted);
        var updated = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(12, updated.LastProcessedMessageId);
        Assert.Equal(3, updated.ImportedMessageCount);
        Assert.Equal(0, await sync.SynchronizeLatestAsync(updated));
        Assert.Equal(3, (await new WorkspaceTaskReader(path).GetTasksAsync(chat)).Count);
    }

    [Fact]
    public async Task SynchronizeLatest_WithoutOwnReactionUsesLastImportedCursor()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat());
        var client = Client();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(5, TASK, false)]);
        var path = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, path);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);
        var cursorReceived = -1;
        client.NewMessagesImpl = (_, cursor, _) =>
        {
            cursorReceived = cursor;
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(6, TASK, null)]);
        };
        Assert.Equal(1, await sync.SynchronizeLatestAsync(chat));
        Assert.Equal(5, cursorReceived);
        Assert.Equal(2, (await new WorkspaceTaskReader(path).GetTasksAsync(chat)).Count);
    }

    [Fact]
    public async Task SynchronizeLatest_WhenTelegramFailsDoesNotAdvanceCursor()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat());
        var client = Client();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(5, TASK, true)]);
        var path = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, path);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);
        client.NewMessagesImpl = (_, _, _) =>
            throw new InvalidOperationException("Telegram offline");
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SynchronizeLatestAsync(chat));
        var saved = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(5, saved.LastProcessedMessageId);
        Assert.Equal(1, saved.ImportedMessageCount);
    }

    private static ConfiguredChat Chat() => new()
    {
        TelegramUserId = 123, PeerId = 101, PeerType = "Channel",
        TelegramTitle = "Пост чат 101", DisplayName = "Пост чат 101",
        SocialNetwork = "Instagram", HistoryDays = 7
    };

    private static FakeTelegramClient Client() => new()
    {
        CurrentTelegramUserId = 123,
        ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
            [new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа")]))
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Incremental-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
