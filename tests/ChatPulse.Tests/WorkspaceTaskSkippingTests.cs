using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTaskSkippingTests
{
    [Fact]
    public async Task Skip_PersistsAndRestoresWithoutChangingTelegramReaction()
    {
        using var temp = new TemporaryDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        await SeedAsync(database);
        var chat = CreateChat(101);
        var store = new WorkspaceTaskSkipStore(database);
        var reader = new WorkspaceTaskReader(database);
        await store.SetSkippedAsync(chat, 1, true);
        var tasks = await reader.GetTasksAsync(chat);
        Assert.True(tasks.Single(task => task.MessageId == 1).IsSkipped);
        Assert.True(tasks.Single(task => task.MessageId == 1).IsCompleted);
        await store.SetSkippedAsync(chat, 1, true); // idempotent
        Assert.True((await new WorkspaceTaskReader(database).GetTasksAsync(chat))
            .Single(task => task.MessageId == 1).IsSkipped);
        await store.SetSkippedAsync(chat, 1, false);
        var restored = (await reader.GetTasksAsync(chat)).Single(task => task.MessageId == 1);
        Assert.False(restored.IsSkipped);
        Assert.True(restored.IsCompleted);
    }

    [Fact]
    public async Task Skip_IsScopedToTelegramAccountAndChatAndMessage()
    {
        using var temp = new TemporaryDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        await SeedAsync(database);
        await new WorkspaceTaskSkipStore(database).SetSkippedAsync(CreateChat(101), 1, true);
        var reader = new WorkspaceTaskReader(database);
        Assert.False((await reader.GetTasksAsync(CreateChat(202))).Single().IsSkipped);
        Assert.True((await reader.GetTasksAsync(CreateChat(101))).Single(task => task.MessageId == 1).IsSkipped);
        Assert.False((await reader.GetTasksAsync(CreateChat(101))).Single(task => task.MessageId == 2).IsSkipped);
        Assert.False((await reader.GetTasksAsync(CreateChat(101, 456))).Single().IsSkipped);
    }

    [Fact]
    public async Task Skip_RejectsUnknownMessageWithoutCreatingGhostStatus()
    {
        using var temp = new TemporaryDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        await SeedAsync(database);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkspaceTaskSkipStore(database).SetSkippedAsync(CreateChat(101), 999, true));
    }

    private static async Task SeedAsync(string database)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ImportedChatMessages (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL, Text TEXT,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            CREATE TABLE ImportedMessageReactionStates (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL, HasOwnReaction INTEGER,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            INSERT INTO ImportedChatMessages VALUES
                (123,'Channel',101,1,'https://www.instagram.com/p/abc/'),
                (123,'Channel',101,2,'https://www.instagram.com/p/def/'),
                (123,'Channel',202,1,'https://www.instagram.com/p/abc/'),
                (456,'Channel',101,1,'https://www.instagram.com/p/abc/');
            INSERT INTO ImportedMessageReactionStates VALUES (123,'Channel',101,1,1);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static ConfiguredChat CreateChat(long peerId, long ownerId = 123) => new()
    {
        TelegramUserId = ownerId, PeerId = peerId, PeerType = "Channel",
        TelegramTitle = "Постчат", DisplayName = "Постчат", SocialNetwork = "Instagram", HistoryDays = 7
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Skips-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
