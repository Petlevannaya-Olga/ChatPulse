using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class CustomEmojiSynchronizationTests
{
    private const string TASK = "✨ Дайте актив https://www.instagram.com/p/example/";

    [Fact]
    public async Task ImportAndRefresh_PreserveTextAndUpdateEmojiEntities()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var configured = CreateChat();
        await settings.SaveChatAsync(configured);
        var client = new FakeTelegramClient
        {
            CurrentTelegramUserId = 123,
            ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
                [new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа")]))
        };
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, TASK, true, "Мария", [new TelegramCustomEmoji(0, 1, 12345)])]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        await sync.SynchronizeAsync(Assert.Single((await settings.LoadAsync()).Chats));
        var first = Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(configured));
        Assert.Contains("12345", first.CustomEmojiJson, StringComparison.Ordinal);
        Assert.Equal(TASK, first.Text);
        Assert.True(first.IsCompleted);

        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(10, "Сообщение изменилось", null, "Мария", [new TelegramCustomEmoji(0, 1, 67890)])]);
        await sync.RefreshReactionsAsync(Assert.Single((await settings.LoadAsync()).Chats));
        var after = Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(configured));
        Assert.Equal(TASK, after.Text); // refresh never rewrites history
        Assert.True(after.IsCompleted); // unknown reaction never cancels confirmed completion
        Assert.Contains("67890", after.CustomEmojiJson, StringComparison.Ordinal);
    }

    private static ConfiguredChat CreateChat() => new()
    {
        TelegramUserId = 123, PeerId = 101, PeerType = "Channel",
        TelegramTitle = "Пост чат 101", DisplayName = "Пост чат 101",
        SocialNetwork = "Instagram", HistoryDays = 7
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Emoji-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
