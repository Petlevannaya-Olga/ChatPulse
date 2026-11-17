using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTaskCompletionTests
{
    private const string TASK = "Реакции на https://www.instagram.com/p/abc123/";

    [Fact]
    public async Task Complete_SendsHeartToOriginalMessageAndPersistsCompletion()
    {
        using var temp = new TemporaryDirectory();
        var (settings, client, sync, chat, database) = await CreateAsync(temp);
        using (settings)
        {
            var tasksBefore = await new WorkspaceTaskReader(database).GetTasksAsync(chat);
            Assert.False(Assert.Single(tasksBefore).IsCompleted);

            await sync.CompleteTaskAsync(chat, 10);
            Assert.Equal(new[] { (101L, 10) }, client.SentHeartReactions);
            var completed = Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat));
            Assert.True(completed.IsCompleted);

            await sync.CompleteTaskAsync(chat, 10);
            Assert.Single(client.SentHeartReactions); // do not replace an existing reaction
        }
    }

    [Fact]
    public async Task Complete_FailedTelegramRequestNeverMarksTaskCompleted()
    {
        using var temp = new TemporaryDirectory();
        var (settings, client, sync, chat, database) = await CreateAsync(temp);
        using (settings)
        {
            client.HeartReactionImpl = (_, _, _) => throw new IOException("offline");
            await Assert.ThrowsAsync<IOException>(() => sync.CompleteTaskAsync(chat, 10));
            Assert.Empty(client.SentHeartReactions);
            Assert.False(Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat)).IsCompleted);
        }
    }

    [Fact]
    public async Task Complete_RejectsSkippedAndNonexistentMessagesBeforeRemoteCall()
    {
        using var temp = new TemporaryDirectory();
        var (settings, client, sync, chat, database) = await CreateAsync(temp);
        using (settings)
        {
            await new WorkspaceTaskSkipStore(database).SetSkippedAsync(chat, 10, true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sync.CompleteTaskAsync(chat, 10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sync.CompleteTaskAsync(chat, 11));
            Assert.Empty(client.SentHeartReactions);
        }
    }

    [Fact]
    public async Task Complete_RejectsChangedTelegramAccountBeforeRemoteCall()
    {
        using var temp = new TemporaryDirectory();
        var (settings, client, sync, chat, database) = await CreateAsync(temp);
        using (settings)
        {
            client.CurrentTelegramUserId = 456;
            await Assert.ThrowsAsync<InvalidOperationException>(() => sync.CompleteTaskAsync(chat, 10));
            Assert.Empty(client.SentHeartReactions);
            Assert.False(Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat)).IsCompleted);
        }
    }

    [Fact]
    public async Task Complete_DoesNotReplaceAnAlreadyConfirmedDifferentReaction()
    {
        using var temp = new TemporaryDirectory();
        var (settings, client, sync, chat, database) = await CreateAsync(temp, reacted: true);
        using (settings)
        {
            await sync.CompleteTaskAsync(chat, 10);
            Assert.Empty(client.SentHeartReactions);
            Assert.True(Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat)).IsCompleted);
        }
    }

    private static async Task<(JsonSettingsService Settings, FakeTelegramClient Client,
        ChatSynchronizationService Sync, ConfiguredChat Chat, string Database)> CreateAsync(
        TemporaryDirectory temp, bool reacted = false)
    {
        var settings = new JsonSettingsService(temp.Path);
        var chat = new ConfiguredChat
        {
            TelegramUserId = 123, PeerId = 101, PeerType = "Channel",
            TelegramTitle = "Пост чат 101", DisplayName = "Пост чат 101",
            SocialNetwork = "Instagram", HistoryDays = 7
        };
        await settings.SaveChatAsync(chat);
        var client = new FakeTelegramClient
        {
            CurrentTelegramUserId = 123,
            ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
                [new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа")])),
            ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
                [new TelegramChatMessage(10, TASK, reacted)])
        };
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        await sync.SynchronizeAsync(chat);
        return (settings, client, sync, chat, database);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Complete-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
