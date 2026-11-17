using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.Views.Onboarding;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class SynchronizationBatchTests
{
    [Fact]
    public void GlobalSynchronization_SelectsOnlyUnfinishedAndChangedChats()
    {
        var unchanged = new SynchronizationChatRow(CreateChat(101, ChatSynchronizationState.COMPLETED));
        var changed = new SynchronizationChatRow(CreateChat(202, ChatSynchronizationState.COMPLETED));
        changed.Days = 20;
        var interrupted = new SynchronizationChatRow(CreateChat(303, ChatSynchronizationState.RUNNING));
        var failed = new SynchronizationChatRow(CreateChat(404, ChatSynchronizationState.FAILED));
        var pending = new SynchronizationChatRow(CreatePending(505));

        var selected = SynchronizationBatchPlan.SelectPending([unchanged, changed, interrupted, failed, pending]);
        Assert.Equal(new long[] { 202, 303, 404, 505 }, selected.Select(row => row.PeerId));
        Assert.False(unchanged.NeedsSynchronization);
        Assert.True(changed.HasPeriodChange);
    }

    [Fact]
    public void ReturningToImportedPeriod_DisablesUnnecessaryReload()
    {
        var row = new SynchronizationChatRow(CreateChat(101, ChatSynchronizationState.COMPLETED));
        row.Days = 30;
        Assert.True(row.NeedsSynchronization);
        row.Days = 7;
        Assert.False(row.NeedsSynchronization);
        Assert.Empty(SynchronizationBatchPlan.SelectPending([row]));
    }

    [Fact]
    public void ReloadFailure_PreservesCompletedSnapshotAndAllowsRetry()
    {
        var row = new SynchronizationChatRow(CreateChat(101, ChatSynchronizationState.COMPLETED));
        row.Days = 15;
        row.SetReloading();
        row.SetReloadFailed("Telegram недоступен");

        Assert.True(row.IsComplete); // Previous successful snapshot is retained.
        Assert.True(row.HasPeriodChange);
        Assert.True(row.NeedsSynchronization); // One shared button can retry.
        Assert.Equal("Не удалось обновить историю", row.Status);
        Assert.Contains("Прежняя история сохранена", row.Details);
        Assert.Equal(3, row.ImportedMessageCount);
    }

    [Fact]
    public async Task CommonBatch_UpdatesOnlyChangedChatWithoutTouchingOtherHistory()
    {
        using var temporary = new TemporaryDirectory();
        using var settings = new JsonSettingsService(temporary.Path);
        await settings.SaveChatAsync(CreateChat(101));
        await settings.SaveChatAsync(CreateChat(202));
        var requests = new List<(long PeerId, int Days)>();
        var client = CreateClient();
        client.ChatMessagesImpl = (chat, days, _) =>
        {
            requests.Add((chat.PeerId, days));
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(10, "первое сообщение")]);
        };
        var database = Path.Combine(temporary.Path, "history.db");
        var synchronization = new ChatSynchronizationService(settings, client, database);
        foreach (var chat in (await settings.LoadAsync()).Chats)
            await synchronization.SynchronizeAsync(chat);
        requests.Clear();

        var rows = (await settings.LoadAsync()).Chats.Select(chat => new SynchronizationChatRow(chat)).ToArray();
        var modified = rows.Single(row => row.PeerId == 101);
        modified.Days = 21;
        await settings.UpdateChatPeriodAsync(modified.Owner, modified.PeerId, modified.PeerType, modified.Days);
        var selected = SynchronizationBatchPlan.SelectPending(rows);
        Assert.Same(modified, Assert.Single(selected));
        var configured = (await settings.LoadAsync()).Chats.Single(chat => chat.PeerId == modified.PeerId);
        client.ChatMessagesImpl = (chat, days, _) =>
        {
            requests.Add((chat.PeerId, days));
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(20, "новое сообщение"), new(21, "ещё сообщение")]);
        };

        foreach (var row in selected)
        {
            var count = await synchronization.ReloadHistoryAsync(configured, row.Days);
            row.SetCompleted(count);
        }

        Assert.Single(requests);
        Assert.Equal((101L, 21), requests[0]);
        Assert.Empty(SynchronizationBatchPlan.SelectPending(rows));
        var saved = (await settings.LoadAsync()).Chats;
        Assert.Equal(21, saved.Single(chat => chat.PeerId == 101).HistoryDays);
        Assert.Null(saved.Single(chat => chat.PeerId == 101).RequestedHistoryDays);
        Assert.Equal(7, saved.Single(chat => chat.PeerId == 202).HistoryDays);

        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ImportedChatMessages WHERE PeerId = 202";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM ImportedChatMessages WHERE PeerId = 101";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SavedPeriodChange_IsStillPendingAfterRestart()
    {
        using var temporary = new TemporaryDirectory();
        using (var settings = new JsonSettingsService(temporary.Path))
        {
            await settings.SaveChatAsync(CreateChat(101, ChatSynchronizationState.COMPLETED));
            await settings.UpdateChatPeriodAsync(123, 101, "Channel", 29);
        }
        using var reopened = new JsonSettingsService(temporary.Path);
        var row = new SynchronizationChatRow(Assert.Single((await reopened.LoadAsync()).Chats));
        Assert.Equal(29, row.Days);
        Assert.True(row.HasPeriodChange);
        Assert.Single(SynchronizationBatchPlan.SelectPending([row]));
    }

    private static ConfiguredChat CreateChat(long id, ChatSynchronizationState state = ChatSynchronizationState.PENDING)
        => new()
        {
            TelegramUserId = 123,
            PeerId = id,
            PeerType = "Channel",
            TelegramTitle = $"Пост чат {id}",
            DisplayName = $"Пост чат {id}",
            SocialNetwork = "Instagram",
            StartDate = DateOnly.FromDateTime(DateTime.Today),
            HistoryDays = 7,
            SynchronizationState = state,
            ImportedMessageCount = state == ChatSynchronizationState.COMPLETED ? 3 : 0
        };

    private static PendingChatConnection CreatePending(long id) => new()
    {
        TelegramUserId = 123,
        PeerId = id,
        PeerType = "Channel",
        TelegramTitle = $"Пост чат {id}",
        SocialNetwork = "Telegram",
        HistoryDays = 7
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
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ChatPulse-Batch-Tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
