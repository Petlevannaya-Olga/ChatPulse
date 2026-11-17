using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>Step 4: fake Telegram transport, real isolated settings.json and SQLite.</summary>
public sealed class OnboardingImportIntegrationCoverageTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    public async Task InitialImport_UsesPreciselyTheUserSelectedPeriod(int days)
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat(101);
        chat.HistoryDays = days;
        await settings.SaveChatAsync(chat);
        var fake = Client();
        var requestedDays = -1;
        fake.ChatMessagesImpl = (_, period, _) =>
        {
            requestedDays = period;
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(100, "Лайк")]);
        };

        var service = new ChatSynchronizationService(settings, fake, temp.Database);
        var count = await service.SynchronizeAsync(chat);

        Assert.Equal(1, count);
        Assert.Equal(days, requestedDays);
        var saved = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(days, saved.HistoryDays);
        Assert.Equal(ChatSynchronizationState.COMPLETED, saved.SynchronizationState);
        Assert.Equal(100L, saved.LastProcessedMessageId);
    }

    [Fact]
    public async Task NoMessages_IsAValidCompletedImportWithZeroCount()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var fake = Client();
        fake.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>([]);

        var result = await new ChatSynchronizationService(settings, fake, temp.Database)
            .SynchronizeAsync(Chat(101));

        var saved = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(0, result);
        Assert.Equal(0, saved.ImportedMessageCount);
        Assert.Null(saved.LastProcessedMessageId);
        Assert.Equal(ChatSynchronizationState.COMPLETED, saved.SynchronizationState);
    }

    [Fact]
    public async Task AlreadyCompletedChat_DoesNotMakeAnotherTelegramRequest()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var fake = Client();
        var calls = 0;
        fake.ChatMessagesImpl = (_, _, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(100, "message")]);
        };
        var service = new ChatSynchronizationService(settings, fake, temp.Database);

        Assert.Equal(1, await service.SynchronizeAsync(Chat(101)));
        Assert.Equal(1, await service.SynchronizeAsync(Chat(101)));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DifferentTelegramAccount_FailsWithoutImportingMessages()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var fake = Client();
        fake.CurrentTelegramUserId = 456;
        var calls = 0;
        fake.ChatMessagesImpl = (_, _, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([]);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ChatSynchronizationService(settings, fake, temp.Database).SynchronizeAsync(Chat(101)));

        Assert.Equal(0, calls);
        Assert.Equal(ChatSynchronizationState.FAILED,
            Assert.Single((await settings.LoadAsync()).Chats).SynchronizationState);
    }

    [Fact]
    public async Task ChatMissingFromTelegramSnapshot_IsRejectedBeforeDownload()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var fake = Client();
        fake.ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123, []));
        var calls = 0;
        fake.ChatMessagesImpl = (_, _, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([]);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ChatSynchronizationService(settings, fake, temp.Database).SynchronizeAsync(Chat(101)));

        Assert.Equal(0, calls);
        Assert.Equal(ChatSynchronizationState.FAILED,
            Assert.Single((await settings.LoadAsync()).Chats).SynchronizationState);
    }

    [Fact]
    public async Task ChatRenamedWithoutStandalonePost_IsNotDownloaded()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var fake = Client();
        fake.ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
            [new TelegramChatOption(101, "Channel", "Репосты", "Группа")]));
        var requests = 0;
        fake.ChatMessagesImpl = (_, _, _) =>
        {
            requests++;
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([]);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ChatSynchronizationService(settings, fake, temp.Database).SynchronizeAsync(Chat(101)));
        Assert.Equal(0, requests);
        Assert.Equal(ChatSynchronizationState.FAILED,
            Assert.Single((await settings.LoadAsync()).Chats).SynchronizationState);
    }

    [Fact]
    public async Task CancelledDownload_LeavesRecoverableRunningStateAndAllowsRetry()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var fake = Client();
        var fail = true;
        fake.ChatMessagesImpl = (_, _, _) =>
        {
            if (fail) throw new OperationCanceledException("Cancelled by navigation");
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(200, "message")]);
        };
        var service = new ChatSynchronizationService(settings, fake, temp.Database);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SynchronizeAsync(Chat(101)));
        var interrupted = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(ChatSynchronizationState.RUNNING, interrupted.SynchronizationState);
        Assert.Null(interrupted.LastProcessedMessageId);

        fail = false;
        Assert.Equal(1, await service.SynchronizeAsync(interrupted));
        var restored = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(ChatSynchronizationState.COMPLETED, restored.SynchronizationState);
        Assert.Equal(200L, restored.LastProcessedMessageId);
    }

    [Fact]
    public async Task TelegramFailure_AllowsRetryWithoutMarkingOnboardingComplete()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var fake = Client();
        var fail = true;
        fake.ChatMessagesImpl = (_, _, _) =>
        {
            if (fail) throw new IOException("offline");
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(100, "message")]);
        };
        var service = new ChatSynchronizationService(settings, fake, temp.Database);

        await Assert.ThrowsAsync<IOException>(() => service.SynchronizeAsync(Chat(101)));
        Assert.Equal(ChatSynchronizationState.FAILED,
            Assert.Single((await settings.LoadAsync()).Chats).SynchronizationState);
        Assert.False((await settings.LoadAsync()).IsOnboardingCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());

        fail = false;
        Assert.Equal(1, await service.SynchronizeAsync(Chat(101)));
        Assert.Equal(ChatSynchronizationState.COMPLETED,
            Assert.Single((await settings.LoadAsync()).Chats).SynchronizationState);
    }

    [Fact]
    public async Task SameMessageIdsFromDifferentChats_AreStoredIndependently()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        await settings.SaveChatAsync(Chat(202));
        var fake = Client();
        fake.ChatMessagesImpl = (chat, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(100, $"message from {chat.PeerId}")]);
        var service = new ChatSynchronizationService(settings, fake, temp.Database);

        foreach (var chat in (await settings.LoadAsync()).Chats)
            await service.SynchronizeAsync(chat);

        await using var connection = new SqliteConnection($"Data Source={temp.Database}");
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ImportedChatMessages";
        Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ReloadWithNewPeriod_ReplacesHistoryButKeepsMonotonicCursor()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var fake = Client();
        fake.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
            new(300, "old"), new(301, "old2")]);
        var service = new ChatSynchronizationService(settings, fake, temp.Database);
        await service.SynchronizeAsync(Chat(101));
        await settings.UpdateChatPeriodAsync(123, 101, "Channel", 20);
        fake.ChatMessagesImpl = (_, period, _) =>
        {
            Assert.Equal(20, period);
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(100, "older")]);
        };

        await service.ReloadHistoryAsync(Chat(101), 20);

        var saved = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(20, saved.HistoryDays);
        Assert.Null(saved.RequestedHistoryDays);
        Assert.Equal(301L, saved.LastProcessedMessageId);
        Assert.Equal(1, saved.ImportedMessageCount);
        await using var connection = new SqliteConnection($"Data Source={temp.Database}");
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MessageId FROM ImportedChatMessages WHERE PeerId=101";
        Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task FailedReloadAfterNetworkLoss_PreservesSuccessfulSnapshotAndRequestedPeriod()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var fake = Client();
        fake.ChatMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(77, "old")]);
        var service = new ChatSynchronizationService(settings, fake, temp.Database);
        await service.SynchronizeAsync(Chat(101));
        await settings.UpdateChatPeriodAsync(123, 101, "Channel", 28);
        fake.ChatMessagesImpl = (_, _, _) => throw new IOException("offline");

        await Assert.ThrowsAsync<IOException>(() => service.ReloadHistoryAsync(Chat(101), 28));

        var saved = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(ChatSynchronizationState.COMPLETED, saved.SynchronizationState);
        Assert.Equal(7, saved.HistoryDays);
        Assert.Equal(28, saved.RequestedHistoryDays);
        Assert.Equal(77L, saved.LastProcessedMessageId);
        await using var connection = new SqliteConnection($"Data Source={temp.Database}");
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MessageId FROM ImportedChatMessages WHERE PeerId=101";
        Assert.Equal(77L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    private static ConfiguredChat Chat(long peerId) => new()
    {
        TelegramUserId = 123, PeerId = peerId, PeerType = "Channel",
        TelegramTitle = $"Пост чат {peerId}", DisplayName = $"Пост чат {peerId}",
        SocialNetwork = "Instagram", HistoryDays = 7
    };

    private static FakeTelegramClient Client() => new()
    {
        CurrentTelegramUserId = 123,
        ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
        [
            new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа"),
            new TelegramChatOption(202, "Channel", "Пост чат 202", "Группа")
        ]))
    };

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Onboarding-Import-{Guid.NewGuid():N}");
        public string Database => System.IO.Path.Combine(Path, "history.db");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
