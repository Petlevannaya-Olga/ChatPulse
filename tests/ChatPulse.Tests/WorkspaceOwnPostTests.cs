using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using ChatPulse.Infrastructure.Telegram;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceOwnPostTests
{
    private const string Post = "Лайк https://www.instagram.com/p/abc123/";

    [Theory]
    [InlineData("@My_Account", "my_account", true)]
    [InlineData("someone_else", "@my_account", false)]
    [InlineData(null, "my_account", null)]
    [InlineData("my_account", null, null)]
    [InlineData("", "my_account", null)]
    public void ComparesActualTelegramUsernames_NotDisplayNames(
        string? sender, string? owner, bool? expected)
        => Assert.Equal(expected, TelegramOwnPostMatch.Compare(sender, owner));

    [Fact]
    public async Task InitialImport_RetainsOwnPostButExcludesItFromAllTaskViewsAndBulkQueue()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = CreateChat();
        await settings.SaveChatAsync(chat);
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
            // The user's Telegram display name is not necessarily their @username.
            new(10, Post, true, "Ольга", AuthorUsername: "Olga_Tg", IsOwnAuthor: true),
            new(11, Post, false, "Ольга", AuthorUsername: "Other_Tg", IsOwnAuthor: false),
            // Without sender username no assumption about ownership is made.
            new(12, Post, false, "Ольга")
        ]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        await sync.SynchronizeAsync(chat);
        var tasks = new WorkspaceTaskReader(database);
        var all = await tasks.GetTasksAsync(chat);
        Assert.Equal(new long[] { 11, 12 }, all.Select(task => task.MessageId));
        Assert.Equal(2, (await tasks.ReadPageAsync(chat, WorkspaceTaskState.All, false, 0, 0)).Total);
        Assert.Equal(2, (await tasks.ReadPageAsync(chat, WorkspaceTaskState.Pending, false, 0, 0)).Total);
        Assert.Equal(0, (await tasks.ReadPageAsync(chat, WorkspaceTaskState.Completed, false, 0, 0)).Total);
        Assert.Null(await tasks.ReadCompletedByIdAsync(chat, 10));

        // Original text and sender metadata remain in the database.
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT Text FROM ImportedChatMessages WHERE MessageId=10";
        Assert.Equal(Post, (string) (await check.ExecuteScalarAsync())!);
        check.CommandText = "SELECT AuthorUsername FROM ImportedMessageAuthorUsernames WHERE MessageId=10";
        Assert.Equal("Olga_Tg", (string)(await check.ExecuteScalarAsync())!);
        check.CommandText = "SELECT IsOwnAuthor FROM ImportedMessageAuthorUsernames WHERE MessageId=10";
        Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ExistingImportedPost_HidesAfterRefresh_WhenTelegramUsernameBecomesKnown()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = CreateChat();
        await settings.SaveChatAsync(chat);
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
            new(10, Post, true, "Ольга")
        ]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        await sync.SynchronizeAsync(chat);
        Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat));

        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
            new(10, "Этот текст не должен заменить импортированный", null, "Ольга",
                AuthorUsername: "Olga_Tg", IsOwnAuthor: true)
        ]);
        await sync.RefreshReactionsAsync(chat);
        Assert.Empty(await new WorkspaceTaskReader(database).GetTasksAsync(chat));
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Text FROM ImportedChatMessages WHERE MessageId=10";
        Assert.Equal(Post, (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task VerifiedNotOwnSender_CanRestoreCardAfterMetadataRefresh()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = CreateChat();
        await settings.SaveChatAsync(chat);
        var client = CreateClient();
        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
            new(10, Post, false, "Ольга", AuthorUsername: "Olga_Tg", IsOwnAuthor: true)
        ]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        await sync.SynchronizeAsync(chat);
        Assert.Empty(await new WorkspaceTaskReader(database).GetTasksAsync(chat));

        client.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
            new(10, Post, false, "Другая", AuthorUsername: "Other_Tg", IsOwnAuthor: false)
        ]);
        await sync.RefreshReactionsAsync(chat);
        Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat));
    }

    [Fact]
    public async Task OwnAuthorFlag_DoesNotLeakAcrossChatsWithSameMessageId()
    {
        using var temp = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat1 = CreateChat();
        var chat2 = CreateChat(202);
        await settings.SaveChatAsync(chat1);
        await settings.SaveChatAsync(chat2);
        var client = CreateClient();
        client.ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123, [
            new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа"),
            new TelegramChatOption(202, "Channel", "Пост чат 202", "Группа")
        ]));
        client.ChatMessagesImpl = (chat, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
            new(10, Post, false, "Ольга", AuthorUsername: chat.PeerId == 101 ? "Olga_Tg" : "Other_Tg",
                IsOwnAuthor: chat.PeerId == 101)
        ]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        await sync.SynchronizeAsync(chat1);
        await sync.SynchronizeAsync(chat2);
        var reader = new WorkspaceTaskReader(database);
        Assert.Empty(await reader.GetTasksAsync(chat1));
        Assert.Single(await reader.GetTasksAsync(chat2));
    }

    private static ConfiguredChat CreateChat(long peerId = 101) => new()
    {
        TelegramUserId = 123, PeerId = peerId, PeerType = "Channel",
        TelegramTitle = $"Пост чат {peerId}", DisplayName = $"Пост чат {peerId}",
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
            $"ChatPulse-OwnPosts-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
