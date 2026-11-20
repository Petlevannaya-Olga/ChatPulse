using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTaskReaderParticipantListsTests
{
    [Fact]
    public async Task TaskReader_SkipsValidAndMalformedParticipantListsWithoutDeletingMessages()
    {
        using var temp = new TemporaryDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        await SeedAsync(database,
        [
            (10, """
                VIP УЧАСТНИКИ
                1 VIP
                https://www.instagram.com/valid_vip
                ВСЕ МЫ
                1
                https://www.instagram.com/valid_member
                """),
            (11, """
                VIP УЧАСТНИКИ
                1 VIP
                https://www.instagram.com/valid_vip
                ВСЕ МЫ
                1
                https://www.instagram.com/valid_member
                ШТРАФНЫЕ ДНИ
                2
                https://www.com/broken_member
                """),
            (12, """
                VIP УЧАСТНИКИ
                https://www.instagram.com/vip_without_number
                ВСЕ МЫ
                https://www.instagram.com/member_without_number
                """),
            (13, "Лайки https://www.instagram.com/p/abc123/"),
        ]);

        var chat = CreateChat("ОРИОН 14 чат");
        var tasks = await new WorkspaceTaskReader(database).GetTasksAsync(chat);
        Assert.Equal(13L, Assert.Single(tasks).MessageId);

        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ImportedChatMessages";
        Assert.Equal(4L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task TaskReader_ExcludesMalformedRegistersInOtherChatsWithoutUsingChat14Rules()
    {
        using var temp = new TemporaryDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        await SeedAsync(database,
        [
            (1, """
                ВСЕ МЫ
                1
                https://www.instagram.com/member_1
                VIP УЧАСТНИКИ
                2
                https://www.invalid-domain.example/vip_2
                """),
            (2, "Оставьте комментарий https://www.instagram.com/p/abc123/"),
        ]);

        // Other chats use the standard candidate recognition; an invalid
        // profile link must not turn a register into an activity task.
        var tasks = await new WorkspaceTaskReader(database)
            .GetTasksAsync(CreateChat("Постчат 13"));
        Assert.Equal(2L, Assert.Single(tasks).MessageId);
    }

    private static async Task SeedAsync(string database, (long Id, string Text)[] messages)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ImportedChatMessages (
                TelegramUserId INTEGER NOT NULL,
                PeerType TEXT NOT NULL,
                PeerId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL,
                Text TEXT,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await command.ExecuteNonQueryAsync();
        foreach (var (id, body) in messages)
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO ImportedChatMessages
                    (TelegramUserId, PeerType, PeerId, MessageId, Text)
                VALUES (123, 'Channel', 101, $id, $text)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$text", body);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static ConfiguredChat CreateChat(string title) => new()
    {
        TelegramUserId = 123,
        PeerId = 101,
        PeerType = "Channel",
        TelegramTitle = title,
        DisplayName = title,
        SocialNetwork = "Instagram",
        SynchronizationState = ChatSynchronizationState.COMPLETED,
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-TaskRegisters-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
