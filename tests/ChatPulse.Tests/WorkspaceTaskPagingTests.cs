using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTaskPagingTests
{
    [Fact]
    public async Task Pages_AreSortedByMessageDate_InEitherDirection_AndHaveStableBoundaries()
    {
        using var temp = new TemporaryDirectory();
        var database = Path.Combine(temp.Path, "tasks.db");
        await SeedAsync(database);
        var reader = new WorkspaceTaskReader(database);
        var chat = CreateChat();

        var newest = await reader.ReadPageAsync(chat, WorkspaceTaskState.All, false, 0, 10);
        var next = await reader.ReadPageAsync(chat, WorkspaceTaskState.All, false, 10, 10,
            includeTotals: false);
        var oldest = await reader.ReadPageAsync(chat, WorkspaceTaskState.All, true, 0, 10);

        Assert.Equal(66, newest.Total);
        Assert.Equal(10, newest.Tasks.Count);
        Assert.Equal(10, next.Tasks.Count);
        Assert.Equal(65L, newest.Tasks.First().MessageId);
        Assert.Equal(56L, newest.Tasks.Last().MessageId);
        Assert.Equal(55L, next.Tasks.First().MessageId);
        Assert.Equal(1L, oldest.Tasks.First().MessageId);
        Assert.Empty(newest.Tasks.Select(task => task.MessageId)
            .Intersect(next.Tasks.Select(task => task.MessageId)));

        // A missing date does not accidentally precede dated messages in either sort direction.
        Assert.Equal(66L, (await reader.ReadPageAsync(chat, WorkspaceTaskState.All,
            false, 65, 1)).Tasks.Single().MessageId);
        Assert.Equal(66L, (await reader.ReadPageAsync(chat, WorkspaceTaskState.All,
            true, 65, 1)).Tasks.Single().MessageId);
    }

    [Fact]
    public async Task FilteringPrecedesPagination_AndUnreadPagesAreNotMaterialized()
    {
        using var temp = new TemporaryDirectory();
        var database = Path.Combine(temp.Path, "tasks.db");
        await SeedAsync(database);
        var reader = new WorkspaceTaskReader(database);
        var chat = CreateChat();

        var completed = await reader.ReadPageAsync(chat, WorkspaceTaskState.Completed, false, 0, 7);
        var second = await reader.ReadPageAsync(chat, WorkspaceTaskState.Completed, false, 7, 7);
        var skipped = await reader.ReadPageAsync(chat, WorkspaceTaskState.Skipped, false, 0, 5);
        var pending = await reader.ReadPageAsync(chat, WorkspaceTaskState.Pending, false, 0, 8);
        var completedIds = Enumerable.Range(1, 65).Where(id => id % 3 == 0 && id % 5 != 0).ToArray();

        Assert.Equal(completedIds.Length, completed.Total);
        Assert.Equal(7, completed.Tasks.Count);
        Assert.All(completed.Tasks.Concat(second.Tasks), item =>
            Assert.True(item.IsCompleted && !item.IsSkipped));
        Assert.Equal(13, skipped.Total);
        Assert.All(skipped.Tasks, item => Assert.True(item.IsSkipped));
        Assert.All(pending.Tasks, item => Assert.True(!item.IsCompleted && !item.IsSkipped));
        Assert.Equal(66 - 13, completed.NonSkippedTotal);
    }

    private static async Task SeedAsync(string database)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE ImportedChatMessages (
                TelegramUserId INTEGER, PeerType TEXT, PeerId INTEGER, MessageId INTEGER, Text TEXT,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            CREATE TABLE ImportedMessageDates (
                TelegramUserId INTEGER, PeerType TEXT, PeerId INTEGER, MessageId INTEGER,
                SentAtUnixSeconds INTEGER,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            CREATE TABLE ImportedMessageReactionStates (
                TelegramUserId INTEGER, PeerType TEXT, PeerId INTEGER, MessageId INTEGER,
                HasOwnReaction INTEGER,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            CREATE TABLE ImportedMessageSkips (
                TelegramUserId INTEGER, PeerType TEXT, PeerId INTEGER, MessageId INTEGER,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            """;
        await schema.ExecuteNonQueryAsync();
        for (var id = 1; id <= 66; id++)
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO ImportedChatMessages VALUES (123, 'Channel', 101, $id, $text);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$text", $"Поставьте лайк https://www.instagram.com/p/test{id}/");
            await insert.ExecuteNonQueryAsync();
            if (id <= 65)
            {
                var date = connection.CreateCommand();
                date.CommandText = "INSERT INTO ImportedMessageDates VALUES (123,'Channel',101,$id,$date)";
                date.Parameters.AddWithValue("$id", id);
                date.Parameters.AddWithValue("$date", 1700000000L + id * 1000L);
                await date.ExecuteNonQueryAsync();
            }
            if (id % 3 == 0)
            {
                var reaction = connection.CreateCommand();
                reaction.CommandText = "INSERT INTO ImportedMessageReactionStates VALUES (123,'Channel',101,$id,1)";
                reaction.Parameters.AddWithValue("$id", id);
                await reaction.ExecuteNonQueryAsync();
            }
            if (id % 5 == 0)
            {
                var skipped = connection.CreateCommand();
                skipped.CommandText = "INSERT INTO ImportedMessageSkips VALUES (123,'Channel',101,$id)";
                skipped.Parameters.AddWithValue("$id", id);
                await skipped.ExecuteNonQueryAsync();
            }
        }
    }

    private static ConfiguredChat CreateChat() => new()
    {
        TelegramUserId = 123, PeerId = 101, PeerType = "Channel",
        TelegramTitle = "Постчат", DisplayName = "Постчат", SocialNetwork = "Instagram",
        SynchronizationState = ChatSynchronizationState.COMPLETED, HistoryDays = 7
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Paging-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
