using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceCompletedTaskHideTests
{
    [Fact]
    public async Task HideCompleted_RemovesOnlyChosenCardAndSurvivesNewReader()
    {
        using var temp = new TemporaryDirectory();
        var db = Path.Combine(temp.Path, "history.db");
        await SeedAsync(db);
        var chat = CreateChat(101);
        var store = new WorkspaceCompletedTaskHideStore(db);
        await store.HideAsync(chat, 1);
        await store.HideAsync(chat, 1);
        var result = await new WorkspaceTaskReader(db).GetTasksAsync(chat);
        Assert.Single(result);
        Assert.Equal(2, result[0].MessageId);

        await using var connection = new SqliteConnection($"Data Source={db}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM ImportedChatMessages),
                   (SELECT HasOwnReaction FROM ImportedMessageReactionStates
                    WHERE PeerId = 101 AND MessageId = 1)
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
    }

    [Fact]
    public async Task HideCompleted_IsScopedToChatAndAccount()
    {
        using var temp = new TemporaryDirectory();
        var db = Path.Combine(temp.Path, "history.db");
        await SeedAsync(db);
        await new WorkspaceCompletedTaskHideStore(db).HideAsync(CreateChat(101), 1);
        var tasks = new WorkspaceTaskReader(db);
        Assert.Single(await tasks.GetTasksAsync(CreateChat(202)));
        Assert.Single(await tasks.GetTasksAsync(CreateChat(101, 456)));
    }

    [Fact]
    public async Task HideCompleted_RejectsUnknownOrUncompletedMessage()
    {
        using var temp = new TemporaryDirectory();
        var db = Path.Combine(temp.Path, "history.db");
        await SeedAsync(db);
        var store = new WorkspaceCompletedTaskHideStore(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.HideAsync(CreateChat(101), 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.HideAsync(CreateChat(101), 999));
        Assert.Equal(2, (await new WorkspaceTaskReader(db).GetTasksAsync(CreateChat(101))).Count);
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
            $"ChatPulse-Hidden-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
