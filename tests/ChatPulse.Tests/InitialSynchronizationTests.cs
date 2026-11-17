using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InitialSynchronizationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    public async Task ChatPeriod_PersistsSeparatelyForEachChat(int days)
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        await settings.SaveChatAsync(CreateChat(202));
        await settings.UpdateChatPeriodAsync(123, 101, "Channel", days);

        var loaded = await settings.LoadAsync();
        Assert.Equal(days, loaded.Chats.Single(chat => chat.PeerId == 101).HistoryDays);
        Assert.Equal(7, loaded.Chats.Single(chat => chat.PeerId == 202).HistoryDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task ChatPeriod_OutsideSupportedRange_IsRejected(int days)
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            settings.UpdateChatPeriodAsync(123, 101, "Channel", days));
        Assert.Equal(7, Assert.Single((await settings.LoadAsync()).Chats).HistoryDays);
    }

    [Fact]
    public async Task Synchronization_StoresMessagesAndNeverDuplicatesThem()
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        var client = CreateClient();
        var calls = 0;
        client.ChatMessagesImpl = (_, days, _) =>
        {
            calls++;
            Assert.Equal(7, days);
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(10, "пост инстаграм"), new(11, "пост телеграм")]);
        };
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, client, database);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(2, await sync.SynchronizeAsync(chat));
        Assert.Equal(2, await sync.SynchronizeAsync(chat));
        Assert.Equal(1, calls);

        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM ImportedChatMessages";
        Assert.Equal(2L, (long)(await query.ExecuteScalarAsync())!);
        Assert.Equal(ChatSynchronizationState.COMPLETED,
            Assert.Single((await settings.LoadAsync()).Chats).SynchronizationState);
    }

    [Fact]
    public async Task FailedImport_CanRetryWithoutResettingOtherChats()
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        await settings.SaveChatAsync(CreateChat(202));
        var client = CreateClient();
        var fail = true;
        client.ChatMessagesImpl = (chat, _, _) =>
        {
            if (chat.PeerId == 202 && fail) throw new IOException("Connection lost");
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(10, "задание")]);
        };
        var sync = new ChatSynchronizationService(settings, client, Path.Combine(temp.Path, "history.db"));
        var all = (await settings.LoadAsync()).Chats;
        await sync.SynchronizeAsync(all[0]);
        await Assert.ThrowsAsync<IOException>(() => sync.SynchronizeAsync(all[1]));
        var states = (await settings.LoadAsync()).Chats;
        Assert.Equal(ChatSynchronizationState.COMPLETED, states[0].SynchronizationState);
        Assert.Equal(ChatSynchronizationState.FAILED, states[1].SynchronizationState);
        Assert.False((await settings.LoadAsync()).IsOnboardingCompleted);

        fail = false;
        await sync.SynchronizeAsync(states[1]);
        states = (await settings.LoadAsync()).Chats;
        Assert.All(states, chat => Assert.Equal(ChatSynchronizationState.COMPLETED, chat.SynchronizationState));
        Assert.False((await settings.LoadAsync()).IsOnboardingCompleted);
    }

    [Fact]
    public async Task Completion_IsBlockedUntilEveryChatHasCompleted()
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        await settings.SaveChatAsync(CreateChat(202));
        await settings.UpdateChatSyncAsync(123, 101, "Channel",
            ChatSynchronizationState.COMPLETED, 3, 10, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());
        Assert.False((await settings.LoadAsync()).IsOnboardingCompleted);
    }

    [Fact]
    public async Task Restart_PreservesStepAndIndividualPeriod()
    {
        using var temp = new TemporarySettingsDirectory();
        using (var settings = new JsonSettingsService(temp.Path))
        {
            await settings.SaveChatAsync(CreateChat(101));
            await settings.UpdateChatPeriodAsync(123, 101, "Channel", 23);
            await settings.SaveOnboardingStepAsync(OnboardingStep.Synchronization);
        }
        using var reopened = new JsonSettingsService(temp.Path);
        var restored = await reopened.LoadAsync();
        Assert.Equal(OnboardingStep.Synchronization, restored.CurrentOnboardingStep);
        Assert.Equal(23, Assert.Single(restored.Chats).HistoryDays);
        Assert.False(restored.IsOnboardingCompleted);
    }

    [Fact]
    public async Task VerifiedCandidates_AreStagedUntilSynchronizationStep()
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [CreatePending(101), CreatePending(202)]);
        var state = await settings.LoadAsync();
        Assert.Empty(state.Chats);
        Assert.Equal(2, state.PendingChats.Count);
        Assert.False(state.IsOnboardingCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());
    }

    [Fact]
    public async Task PendingCandidates_PersistPeriodsAndResumeAfterRestart()
    {
        using var temp = new TemporarySettingsDirectory();
        using (var settings = new JsonSettingsService(temp.Path))
        {
            await settings.SavePendingChatsAsync(123, [CreatePending(101), CreatePending(202)]);
            await settings.UpdatePendingChatPeriodAsync(123, 101, "Channel", 30);
            await settings.SaveOnboardingStepAsync(OnboardingStep.Synchronization);
        }
        using var restored = new JsonSettingsService(temp.Path);
        var state = await restored.LoadAsync();
        Assert.Equal(OnboardingStep.Synchronization, state.CurrentOnboardingStep);
        Assert.Equal(30, state.PendingChats.Single(c => c.PeerId == 101).HistoryDays);
        Assert.Equal(7, state.PendingChats.Single(c => c.PeerId == 202).HistoryDays);
        Assert.Empty(state.Chats);
    }

    [Fact]
    public async Task PendingCandidates_RejectNonPostTitlesAndInvalidPeriod()
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var invalid = CreatePending(101);
        invalid.TelegramTitle = "Репосты";
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SavePendingChatsAsync(123, [invalid]));
        await settings.SavePendingChatsAsync(123, [CreatePending(101)]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            settings.UpdatePendingChatPeriodAsync(123, 101, "Channel", 31));
    }

    [Fact]
    public async Task ReturningToReview_PreservesConfiguredPendingPeriod()
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [CreatePending(101)]);
        await settings.UpdatePendingChatPeriodAsync(123, 101, "Channel", 18);
        await settings.SavePendingChatsAsync(123, [CreatePending(101)]);
        Assert.Equal(18, Assert.Single((await settings.LoadAsync()).PendingChats).HistoryDays);
    }

    [Fact]
    public async Task ConfiguringPendingPeriods_DoesNotConnectOrImportChats()
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [CreatePending(101), CreatePending(202)]);
        await settings.SaveOnboardingStepAsync(OnboardingStep.Synchronization);

        // Selecting a period is configuration only: no SaveChat and no Telegram import.
        await settings.UpdatePendingChatPeriodAsync(123, 101, "Channel", 14);
        await settings.UpdatePendingChatPeriodAsync(123, 202, "Channel", 3);

        using var reopened = new JsonSettingsService(temp.Path);
        var restored = await reopened.LoadAsync();
        Assert.Empty(restored.Chats);
        Assert.Equal(2, restored.PendingChats.Count);
        Assert.Equal(14, restored.PendingChats.Single(chat => chat.PeerId == 101).HistoryDays);
        Assert.Equal(3, restored.PendingChats.Single(chat => chat.PeerId == 202).HistoryDays);
        Assert.Equal(OnboardingStep.Synchronization, restored.CurrentOnboardingStep);
        Assert.False(restored.IsOnboardingCompleted);
    }

    [Fact]
    public async Task ExistingConfiguredChat_IsNotStagedAgain()
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(CreateChat(101));
        await settings.SavePendingChatsAsync(123, [CreatePending(101), CreatePending(202)]);
        var state = await settings.LoadAsync();
        Assert.Single(state.Chats);
        Assert.Equal(202, Assert.Single(state.PendingChats).PeerId);
    }

    [Fact]
    public async Task PendingCandidate_IsRemovedOnlyAfterSuccessfulConnection()
    {
        using var temp = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [CreatePending(101)]);
        await settings.SaveChatAsync(CreateChat(101));
        await settings.RemovePendingChatAsync(123, 101, "Channel");
        var state = await settings.LoadAsync();
        Assert.Empty(state.PendingChats);
        Assert.Single(state.Chats);
    }

    private static PendingChatConnection CreatePending(long id) => new()
    {
        TelegramUserId = 123, PeerId = id, PeerType = "Channel",
        TelegramTitle = $"Пост чат {id}", SocialNetwork = "Instagram", HistoryDays = 7
    };

    private sealed class TemporarySettingsDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ChatPulse-Sync-Tests-{Guid.NewGuid():N}");
        public TemporarySettingsDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    private static ConfiguredChat CreateChat(long peerId) => new()
    {
        TelegramUserId = 123,
        PeerId = peerId,
        PeerType = "Channel",
        TelegramTitle = $"Пост чат {peerId}",
        DisplayName = $"Пост чат {peerId}",
        SocialNetwork = "Instagram",
        StartDate = DateOnly.FromDateTime(DateTime.Today),
    };

    private static FakeTelegramClient CreateClient() => new()
    {
        CurrentTelegramUserId = 123,
        ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
            [new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа"),
             new TelegramChatOption(202, "Channel", "Пост чат 202", "Группа")]))
    };
}
