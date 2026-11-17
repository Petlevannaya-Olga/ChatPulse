using System.IO;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>File-backed onboarding state, isolated per test in a temporary directory.</summary>
public sealed class OnboardingSettingsCoverageTests
{
    [Fact]
    public async Task FirstLaunch_ReturnsCleanOnboardingDefaultsWithoutCreatingAFile()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var loaded = await settings.LoadAsync();

        Assert.Equal(OnboardingStep.Welcome, loaded.CurrentOnboardingStep);
        Assert.Equal(TelegramOnboardingPage.ApiCredentials, loaded.CurrentTelegramPage);
        Assert.False(loaded.IsOnboardingCompleted);
        Assert.Null(loaded.UserName);
        Assert.Empty(loaded.Chats);
        Assert.Empty(loaded.PendingChats);
        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, "settings.json")));
    }

    [Theory]
    [InlineData(OnboardingStep.Welcome)]
    [InlineData(OnboardingStep.Profile)]
    [InlineData(OnboardingStep.Telegram)]
    [InlineData(OnboardingStep.FirstChat)]
    [InlineData(OnboardingStep.Synchronization)]
    [InlineData(OnboardingStep.Workspace)]
    public async Task EveryOnboardingStep_RestoresAfterRecreatingSettingsService(OnboardingStep step)
    {
        using var temp = new TestDirectory();
        using (var settings = new JsonSettingsService(temp.Path))
            await settings.SaveOnboardingStepAsync(step);

        using var reopened = new JsonSettingsService(temp.Path);
        Assert.Equal(step, (await reopened.LoadAsync()).CurrentOnboardingStep);
    }

    [Fact]
    public async Task SavingProfile_TrimsNameAndMovesToTelegram()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveTelegramPageAsync(TelegramOnboardingPage.Phone);

        await settings.SaveUserNameAndStepAsync("  Ольга  ", OnboardingStep.Telegram);

        var stored = await settings.LoadAsync();
        Assert.Equal("Ольга", stored.UserName);
        Assert.Equal(OnboardingStep.Telegram, stored.CurrentOnboardingStep);
        Assert.Equal(TelegramOnboardingPage.ApiCredentials, stored.CurrentTelegramPage);
    }

    [Fact]
    public async Task ReturningToProfile_DoesNotEraseTelegramSubpage()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveTelegramPageAsync(TelegramOnboardingPage.Phone);
        await settings.SaveUserNameAndStepAsync("Ольга", OnboardingStep.Profile);

        Assert.Equal(TelegramOnboardingPage.Phone, (await settings.LoadAsync()).CurrentTelegramPage);
    }

    [Fact]
    public async Task TelegramSubpage_RestoresAcrossRestart()
    {
        using var temp = new TestDirectory();
        using (var settings = new JsonSettingsService(temp.Path))
            await settings.SaveTelegramPageAsync(TelegramOnboardingPage.Phone);

        using var reopened = new JsonSettingsService(temp.Path);
        Assert.Equal(TelegramOnboardingPage.Phone, (await reopened.LoadAsync()).CurrentTelegramPage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(-1)]
    public async Task InvalidPendingPeriod_IsRejectedWithoutChangingSavedValue(int days)
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [Pending(101)]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            settings.UpdatePendingChatPeriodAsync(123, 101, "Channel", days));

        Assert.Equal(7, Assert.Single((await settings.LoadAsync()).PendingChats).HistoryDays);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task PendingPeriod_BoundaryValuesAreSavedWithoutConnecting(int days)
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [Pending(101)]);
        await settings.UpdatePendingChatPeriodAsync(123, 101, "Channel", days);

        var saved = await settings.LoadAsync();
        Assert.Equal(days, Assert.Single(saved.PendingChats).HistoryDays);
        Assert.Empty(saved.Chats);
        Assert.False(saved.IsOnboardingCompleted);
    }

    [Theory]
    [InlineData("Чат подписок", "Instagram", 7, 123, 101, "Channel")]
    [InlineData("Репосты", "Instagram", 7, 123, 101, "Channel")]
    [InlineData("Пост чат", "TikTok", 7, 123, 101, "Channel")]
    [InlineData("Пост чат", "Instagram", 0, 123, 101, "Channel")]
    [InlineData("Пост чат", "Instagram", 31, 123, 101, "Channel")]
    [InlineData("Пост чат", "Instagram", 7, 0, 101, "Channel")]
    [InlineData("Пост чат", "Instagram", 7, 123, 0, "Channel")]
    [InlineData("Пост чат", "Instagram", 7, 123, 101, "")]
    public async Task InvalidCandidate_IsNeverSaved(string title, string network, int days,
        long owner, long peerId, string peerType)
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var candidate = Pending(peerId, owner, peerType);
        candidate.TelegramTitle = title;
        candidate.SocialNetwork = network;
        candidate.HistoryDays = days;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            settings.SavePendingChatsAsync(123, [candidate]));
        Assert.Empty((await settings.LoadAsync()).PendingChats);
    }

    [Fact]
    public async Task EmptyCandidateList_CannotAdvanceChatSelection()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            settings.SavePendingChatsAsync(123, Array.Empty<PendingChatConnection>()));
        Assert.Empty((await settings.LoadAsync()).PendingChats);
    }

    [Fact]
    public async Task DuplicatedCandidate_IsRejectedBeforeAnyWrite()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            settings.SavePendingChatsAsync(123, [Pending(101), Pending(101)]));
        Assert.Empty((await settings.LoadAsync()).PendingChats);
    }

    [Fact]
    public async Task SamePeerWithDifferentPeerTypes_CanBeSelected()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [Pending(101, peerType: "Channel"), Pending(101, peerType: "Chat")]);

        Assert.Equal(2, (await settings.LoadAsync()).PendingChats.Count);
    }

    [Fact]
    public async Task RefreshingSelection_PreservesUpdatedPeriodsAndRemovesDeselectedCandidate()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [Pending(101), Pending(202)]);
        await settings.UpdatePendingChatPeriodAsync(123, 101, "Channel", 24);

        await settings.SavePendingChatsAsync(123, [Pending(101), Pending(303)]);

        var rows = (await settings.LoadAsync()).PendingChats;
        Assert.Equal(2, rows.Count);
        Assert.Equal(24, rows.Single(c => c.PeerId == 101).HistoryDays);
        Assert.Equal(7, rows.Single(c => c.PeerId == 303).HistoryDays);
        Assert.DoesNotContain(rows, c => c.PeerId == 202);
    }

    [Fact]
    public async Task SelectionUpdateForOneAccount_DoesNotRemoveAnotherAccountsCandidates()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [Pending(101)]);
        await settings.SavePendingChatsAsync(456, [Pending(202, 456)]);

        await settings.SavePendingChatsAsync(123, [Pending(303)]);

        var rows = (await settings.LoadAsync()).PendingChats;
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.TelegramUserId == 456 && row.PeerId == 202);
        Assert.Contains(rows, row => row.TelegramUserId == 123 && row.PeerId == 303);
    }

    [Fact]
    public async Task RemovingCandidate_IsIdempotentAndScopedToAccount()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [Pending(101)]);
        await settings.SavePendingChatsAsync(456, [Pending(101, 456)]);

        await settings.RemovePendingChatAsync(123, 101, "Channel");
        await settings.RemovePendingChatAsync(123, 101, "Channel");

        var remaining = Assert.Single((await settings.LoadAsync()).PendingChats);
        Assert.Equal(456L, remaining.TelegramUserId);
    }

    [Fact]
    public async Task ResavingChatMetadata_PreservesImportedSnapshotAndPendingReload()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        await settings.UpdateChatSyncAsync(123, 101, "Channel", ChatSynchronizationState.COMPLETED, 45, 900, null);
        await settings.UpdateChatPeriodAsync(123, 101, "Channel", 20);
        var metadata = Chat(101);
        metadata.DisplayName = "Новое имя чата";
        metadata.HistoryDays = 2;

        await settings.SaveChatAsync(metadata);

        var reloaded = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal("Новое имя чата", reloaded.DisplayName);
        Assert.Equal(7, reloaded.HistoryDays);
        Assert.Equal(20, reloaded.RequestedHistoryDays);
        Assert.Equal(ChatSynchronizationState.COMPLETED, reloaded.SynchronizationState);
        Assert.Equal(45, reloaded.ImportedMessageCount);
        Assert.Equal(900L, reloaded.LastProcessedMessageId);
    }

    [Fact]
    public async Task ResavingChat_DoesNotOverwriteOtherAccountsIdenticalPeer()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        await settings.SaveChatAsync(Chat(101, 456));
        var updated = Chat(101);
        updated.DisplayName = "Переименован";

        await settings.SaveChatAsync(updated);

        var rows = (await settings.LoadAsync()).Chats;
        Assert.Equal(2, rows.Count);
        Assert.Equal("Переименован", rows.Single(c => c.TelegramUserId == 123).DisplayName);
        Assert.Equal("Пост чат 101", rows.Single(c => c.TelegramUserId == 456).DisplayName);
    }

    [Fact]
    public async Task CompletingOnboarding_WithoutAnyChatIsForbidden()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveOnboardingStepAsync(OnboardingStep.Synchronization);

        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());

        var state = await settings.LoadAsync();
        Assert.False(state.IsOnboardingCompleted);
        Assert.Equal(OnboardingStep.Synchronization, state.CurrentOnboardingStep);
    }

    [Theory]
    [InlineData(ChatSynchronizationState.PENDING)]
    [InlineData(ChatSynchronizationState.RUNNING)]
    [InlineData(ChatSynchronizationState.FAILED)]
    public async Task IncompleteChat_CannotCompleteOnboarding(ChatSynchronizationState state)
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        await settings.UpdateChatSyncAsync(123, 101, "Channel", state, 0, null, "error");

        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());
        Assert.False((await settings.LoadAsync()).IsOnboardingCompleted);
    }

    [Fact]
    public async Task PendingCandidate_PreventsCompletingEvenIfExistingChatsAreReady()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        await settings.UpdateChatSyncAsync(123, 101, "Channel", ChatSynchronizationState.COMPLETED, 5, 42, null);
        await settings.SavePendingChatsAsync(123, [Pending(202)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());
        Assert.False((await settings.LoadAsync()).IsOnboardingCompleted);
    }

    [Fact]
    public async Task RequestedDifferentPeriod_PreventsCompletingUntilSuccessfulReload()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        await settings.UpdateChatSyncAsync(123, 101, "Channel", ChatSynchronizationState.COMPLETED, 5, 42, null);
        await settings.UpdateChatPeriodAsync(123, 101, "Channel", 20);

        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());
        Assert.False((await settings.LoadAsync()).IsOnboardingCompleted);

        await settings.ConfirmChatHistoryReloadAsync(123, 101, "Channel", 20, 8, 52);
        await settings.CompleteOnboardingAsync();
        var state = await settings.LoadAsync();
        Assert.True(state.IsOnboardingCompleted);
        Assert.Equal(OnboardingStep.Workspace, state.CurrentOnboardingStep);
    }

    [Fact]
    public async Task CompletedPeriod_ReturnedToOriginalValue_DoesNotRequireReload()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        await settings.UpdateChatSyncAsync(123, 101, "Channel", ChatSynchronizationState.COMPLETED, 5, 42, null);
        await settings.UpdateChatPeriodAsync(123, 101, "Channel", 20);
        await settings.UpdateChatPeriodAsync(123, 101, "Channel", 7);

        Assert.Null(Assert.Single((await settings.LoadAsync()).Chats).RequestedHistoryDays);
        await settings.CompleteOnboardingAsync();
        Assert.True((await settings.LoadAsync()).IsOnboardingCompleted);
    }

    [Fact]
    public async Task ConcurrentStepAndChatUpdates_DoNotLoseEachOthersChanges()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SavePendingChatsAsync(123, [Pending(101), Pending(202)]);

        await Task.WhenAll(
            settings.SaveUserNameAndStepAsync("Ольга", OnboardingStep.Synchronization),
            settings.UpdatePendingChatPeriodAsync(123, 101, "Channel", 17),
            settings.UpdatePendingChatPeriodAsync(123, 202, "Channel", 29));

        var state = await settings.LoadAsync();
        Assert.Equal("Ольга", state.UserName);
        Assert.Equal(OnboardingStep.Synchronization, state.CurrentOnboardingStep);
        Assert.Equal(17, state.PendingChats.Single(chat => chat.PeerId == 101).HistoryDays);
        Assert.Equal(29, state.PendingChats.Single(chat => chat.PeerId == 202).HistoryDays);
    }

    [Fact]
    public async Task InvalidPeriodUpdate_DoesNotCorruptPreviouslySavedSettings()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveUserNameAndStepAsync("Ольга", OnboardingStep.Synchronization);
        await settings.SaveChatAsync(Chat(101));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            settings.UpdateChatPeriodAsync(123, 101, "Channel", 99));

        using var reopened = new JsonSettingsService(temp.Path);
        var saved = await reopened.LoadAsync();
        Assert.Equal("Ольга", saved.UserName);
        Assert.Equal(OnboardingStep.Synchronization, saved.CurrentOnboardingStep);
        Assert.Equal(7, Assert.Single(saved.Chats).HistoryDays);
    }

    [Fact]
    public async Task AccountOwnerMismatch_CannotEditOthersChatPeriod()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.UpdateChatPeriodAsync(456, 101, "Channel", 17));

        Assert.Equal(7, Assert.Single((await settings.LoadAsync()).Chats).HistoryDays);
    }

    [Fact]
    public async Task FailedReloadConfirmation_DoesNotChangeSnapshot()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        await settings.UpdateChatSyncAsync(123, 101, "Channel", ChatSynchronizationState.FAILED, 3, 42, "error");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.ConfirmChatHistoryReloadAsync(123, 101, "Channel", 20, 9, 99));

        var saved = Assert.Single((await settings.LoadAsync()).Chats);
        Assert.Equal(7, saved.HistoryDays);
        Assert.Equal(3, saved.ImportedMessageCount);
        Assert.Equal(42L, saved.LastProcessedMessageId);
        Assert.Equal(ChatSynchronizationState.FAILED, saved.SynchronizationState);
    }

    private static PendingChatConnection Pending(long peerId, long owner = 123, string peerType = "Channel")
        => new()
        {
            TelegramUserId = owner, PeerId = peerId, PeerType = peerType,
            TelegramTitle = $"Пост чат {peerId}", SocialNetwork = "Instagram", HistoryDays = 7
        };

    private static ConfiguredChat Chat(long peerId, long owner = 123) => new()
    {
        TelegramUserId = owner, PeerId = peerId, PeerType = "Channel",
        TelegramTitle = $"Пост чат {peerId}", DisplayName = $"Пост чат {peerId}",
        SocialNetwork = "Instagram", HistoryDays = 7
    };

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ChatPulse-Onboarding-Settings-{Guid.NewGuid():N}");

        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
