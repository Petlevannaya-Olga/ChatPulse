using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using ChatPulse.Infrastructure.Telegram;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ReactionSynchronizationTests
{
    private const string TASK = "Лайки на https://www.instagram.com/p/abc123/";

    [Fact]
    public async Task InitialImport_StoresOwnReactionAndReaderShowsCompleted()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, true), new(11, TASK, false), new(12, TASK, null)]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        await sync.SynchronizeAsync(Assert.Single((await settings.LoadAsync()).Chats));
        var tasks = await new WorkspaceTaskReader(database).GetTasksAsync(CreateChat(101));
        Assert.Equal(3, tasks.Count);
        Assert.True(tasks.Single(item => item.MessageId == 10).IsCompleted);
        Assert.False(tasks.Single(item => item.MessageId == 11).IsCompleted);
        Assert.False(tasks.Single(item => item.MessageId == 12).IsCompleted);
    }

    [Fact]
    public async Task Refresh_RechecksPreviousMessagesWithoutReimportOrCursorChange()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, false)]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);
        var before = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.False(Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat)).IsCompleted);

        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, true)]);
        Assert.Equal(1, await sync.RefreshReactionsAsync(before));
        Assert.True(Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat)).IsCompleted);
        var after = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(before.LastProcessedMessageId, after.LastProcessedMessageId);
        Assert.Equal(before.ImportedMessageCount, after.ImportedMessageCount);
        Assert.Equal(ChatSynchronizationState.COMPLETED, after.SynchronizationState);
    }

    [Fact]
    public async Task UnknownReactions_DoNotResetSavedCompletion()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, true)]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, null)]);
        Assert.Equal(0, await sync.RefreshReactionsAsync(chat));
        Assert.True(Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat)).IsCompleted);
    }

    [Fact]
    public async Task ConfirmedReactionRemoval_MovesTaskBackToPending()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, true)]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, false)]);
        await sync.RefreshReactionsAsync(chat);
        Assert.False(Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat)).IsCompleted);
    }

    [Fact]
    public async Task RefreshDoesNotInsertNewMessagesOrMixUpChats()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        await settings.SaveChatAsync(CreateChat(202));
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, false)]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        var chats = (await settings.LoadAsync()).Chats;
        await sync.SynchronizeAsync(chats[0]);
        await sync.SynchronizeAsync(chats[1]);
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, true), new(99, TASK, true)]);
        await sync.RefreshReactionsAsync(chats[0]);
        var reader = new WorkspaceTaskReader(database);
        var first = await reader.GetTasksAsync(chats[0]);
        var second = await reader.GetTasksAsync(chats[1]);
        Assert.Single(first);
        Assert.True(first[0].IsCompleted);
        Assert.Single(second);
        Assert.False(second[0].IsCompleted);
    }

    [Fact]
    public async Task TaskReader_SkipsMessagesWithoutLinks_AndKeepsCompleteTextAndAuthor()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        var client = CreateClient();
        const string fullText = "Добрый день!\nПожалуйста, оставьте комментарий ❤️\nhttps://www.instagram.com/p/abc123/";
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, fullText, false, "Мария Иванова"), new(11, "Объявление без ссылки", false, "@admin")]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);
        var tasks = await new WorkspaceTaskReader(database).GetTasksAsync(chat);
        Assert.Single(tasks);
        var first = Assert.Single(tasks);
        Assert.Equal(fullText, first.Text);
        Assert.Equal("Мария Иванова", first.AuthorName);
        Assert.Equal("https://t.me/c/101/10", first.SourceUrl);
        Assert.Equal("https://www.instagram.com/p/abc123/", first.PostUrl);
        Assert.DoesNotContain(tasks, task => task.MessageId == 11);
    }

    [Fact]
    public async Task ReactionRefresh_UpdatesAuthorWithoutRewritingHistory()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        var client = CreateClient();
        const string originalText = "Текст из первого импорта https://www.instagram.com/p/abc123/";
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, originalText, true)]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, "Изменённый в Telegram текст https://www.instagram.com/p/changed/", null, "Анна")]);
        await sync.RefreshReactionsAsync(chat);
        var task = Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat));
        Assert.Equal(originalText, task.Text);
        Assert.Equal("Анна", task.AuthorName);
        Assert.True(task.IsCompleted);
    }

    [Fact]
    public async Task UnknownAuthorOnRefresh_DoesNotOverwritePreviouslyKnownName()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, false, "Мария Иванова")]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);

        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, null, "Автор неизвестен")]);
        await sync.RefreshReactionsAsync(chat);
        var task = Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat));
        Assert.Equal("Мария Иванова", task.AuthorName);
    }

    [Fact]
    public void ReactionEvidence_RecognizesOnlyCurrentAccountChosenFlag()
    {
        var msg = new TestMessage { reactions = new TestReactions
        {
            min = false, results = [new TestCount { chosen_order = 0 }]
        }};
        Assert.True(TelegramOwnReactionEvidence.ReadEvidence(msg));
        msg.reactions.results = [new TestCount { chosen_order = null }];
        Assert.False(TelegramOwnReactionEvidence.ReadEvidence(msg));
        msg.reactions.min = true;
        Assert.Null(TelegramOwnReactionEvidence.ReadEvidence(msg));
        msg.reactions = null;
        Assert.Null(TelegramOwnReactionEvidence.ReadEvidence(msg));
    }

    private sealed class TestMessage { public TestReactions? reactions; }
    private sealed class TestReactions { public bool min; public TestCount[] results = []; }
    private sealed class TestCount { public int? chosen_order; }

    private static ConfiguredChat CreateChat(long id) => new()
    {
        TelegramUserId = 123, PeerId = id, PeerType = "Channel",
        TelegramTitle = $"Пост чат {id}", DisplayName = $"Пост чат {id}",
        SocialNetwork = "Instagram", HistoryDays = 7
    };

    private static FakeTelegramClient CreateClient() => new()
    {
        CurrentTelegramUserId = 123,
        ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
        [new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа"),
         new TelegramChatOption(202, "Channel", "Пост чат 202", "Группа")]))
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Reactions-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
