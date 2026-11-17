using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>Step 4: state machine behind the common synchronization button.</summary>
public sealed class OnboardingSynchronizationStateCoverageTests
{
    [Fact]
    public void PendingCandidate_RequiresImportAndKeepsVerifiedNetwork()
    {
        var row = new SynchronizationChatRow(Pending(101));

        Assert.True(row.IsPendingConnection);
        Assert.True(row.NeedsSynchronization);
        Assert.False(row.IsComplete);
        Assert.True(row.CanEditPeriod);
        Assert.Equal("Telegram", row.Network);
        Assert.Equal(7, row.Days);
        Assert.Equal(30, row.AvailableDays.Length);
        Assert.Equal(1, row.AvailableDays[0]);
        Assert.Equal(30, row.AvailableDays[^1]);
    }

    [Fact]
    public void ChangingPendingCandidatePeriod_DoesNotConnectIt()
    {
        var row = new SynchronizationChatRow(Pending(101));
        row.Days = 30;

        Assert.Equal(30, row.Days);
        Assert.True(row.IsPendingConnection);
        Assert.True(row.NeedsSynchronization);
        Assert.False(row.HasPeriodChange);
    }

    [Fact]
    public void FailedState_IsRetryableButPeriodCannotChangeDuringRecovery()
    {
        var row = new SynchronizationChatRow(Configured(101, ChatSynchronizationState.FAILED));
        row.Days = 30;

        Assert.Equal(7, row.Days);
        Assert.True(row.CanRetry);
        Assert.True(row.NeedsSynchronization);
        Assert.False(row.IsComplete);
        Assert.False(row.CanEditPeriod);
    }

    [Fact]
    public void InterruptedState_ResumesUsingSavedPeriod()
    {
        var row = new SynchronizationChatRow(Configured(101, ChatSynchronizationState.RUNNING));

        Assert.True(row.CanRetry);
        Assert.True(row.NeedsSynchronization);
        Assert.False(row.IsComplete);
        Assert.Equal(7, row.Days);
    }

    [Fact]
    public void CompletedChat_IsExcludedFromCommonBatchUntilPeriodChanges()
    {
        var row = new SynchronizationChatRow(Configured(101, ChatSynchronizationState.COMPLETED));

        Assert.True(row.IsComplete);
        Assert.False(row.NeedsSynchronization);
        Assert.Empty(SynchronizationBatchPlan.SelectPending([row]));

        row.Days = 21;

        Assert.True(row.IsComplete);
        Assert.True(row.HasPeriodChange);
        Assert.Same(row, Assert.Single(SynchronizationBatchPlan.SelectPending([row])));
    }

    [Fact]
    public void RestoringSavedRequestedPeriod_RequiresExplicitReload()
    {
        var chat = Configured(101, ChatSynchronizationState.COMPLETED);
        chat.RequestedHistoryDays = 20;
        var row = new SynchronizationChatRow(chat);

        Assert.Equal(20, row.Days);
        Assert.True(row.HasPeriodChange);
        Assert.True(row.NeedsSynchronization);
    }

    [Fact]
    public void RestoringSameRequestedPeriod_DoesNotQueueUnnecessaryReload()
    {
        var chat = Configured(101, ChatSynchronizationState.COMPLETED);
        chat.RequestedHistoryDays = 7;
        var row = new SynchronizationChatRow(chat);

        Assert.False(row.HasPeriodChange);
        Assert.False(row.NeedsSynchronization);
    }

    [Fact]
    public void InvalidSliderValues_AreIgnored()
    {
        var row = new SynchronizationChatRow(Pending(101));
        row.Days = 0;
        Assert.Equal(7, row.Days);
        row.Days = 31;
        Assert.Equal(7, row.Days);
    }

    [Fact]
    public void ReturningToPreviouslyLoadedPeriod_CancelsReload()
    {
        var row = new SynchronizationChatRow(Configured(101, ChatSynchronizationState.COMPLETED));
        row.Days = 14;
        row.Days = 7;

        Assert.False(row.HasPeriodChange);
        Assert.False(row.NeedsSynchronization);
        Assert.Empty(SynchronizationBatchPlan.SelectPending([row]));
    }

    [Fact]
    public void RunningChat_DisablesEditingUntilOperationCompletes()
    {
        var row = new SynchronizationChatRow(Pending(101));
        row.SetWorking("Загружаем…");
        row.Days = 20;

        Assert.False(row.CanEditPeriod);
        Assert.Equal(7, row.Days);
        Assert.True(row.CanRetry);
        Assert.True(row.NeedsSynchronization);
    }

    [Fact]
    public void SuccessfulImport_EnablesContinueWithoutReload()
    {
        var row = new SynchronizationChatRow(Pending(101));
        row.MarkConnected();
        row.SetWorking("Загружаем…");
        row.SetCompleted(37);

        Assert.False(row.IsPendingConnection);
        Assert.True(row.IsComplete);
        Assert.False(row.NeedsSynchronization);
        Assert.False(row.CanRetry);
        Assert.Equal(37, row.ImportedMessageCount);
        Assert.Empty(SynchronizationBatchPlan.SelectPending([row]));
    }

    [Fact]
    public void FailedInitialImport_CanRetryThroughSharedButton()
    {
        var row = new SynchronizationChatRow(Pending(101));
        row.SetWorking("Загрузка");
        row.SetFailed("Telegram недоступен");

        Assert.False(row.IsComplete);
        Assert.True(row.CanRetry);
        Assert.Same(row, Assert.Single(SynchronizationBatchPlan.SelectPending([row])));
    }

    [Fact]
    public void FailedReload_PreservesPreviousCompleteStatusAndMessageCount()
    {
        var row = new SynchronizationChatRow(Configured(101, ChatSynchronizationState.COMPLETED));
        row.Days = 23;
        row.SetReloading();
        row.SetReloadFailed("Сеть недоступна");

        Assert.True(row.IsComplete);
        Assert.Equal(5, row.ImportedMessageCount);
        Assert.True(row.NeedsSynchronization);
        Assert.True(row.HasPeriodChange);
        Assert.Contains("Прежняя история сохранена", row.Details);
    }

    [Fact]
    public void SuccessfulReload_ResetsOutstandingPeriodChange()
    {
        var row = new SynchronizationChatRow(Configured(101, ChatSynchronizationState.COMPLETED));
        row.Days = 23;
        row.SetReloading();
        row.SetCompleted(19);

        Assert.Equal(23, row.Days);
        Assert.False(row.HasPeriodChange);
        Assert.False(row.NeedsSynchronization);
        Assert.Equal(19, row.ImportedMessageCount);
    }

    [Fact]
    public void CommonBatch_SelectsOnlyNewFailedInterruptedAndChangedChatsInOrder()
    {
        var completed = new SynchronizationChatRow(Configured(101, ChatSynchronizationState.COMPLETED));
        var pending = new SynchronizationChatRow(Pending(202));
        var failed = new SynchronizationChatRow(Configured(303, ChatSynchronizationState.FAILED));
        var changed = new SynchronizationChatRow(Configured(404, ChatSynchronizationState.COMPLETED));
        changed.Days = 12;

        Assert.Equal(new long[] { 202, 303, 404 }, SynchronizationBatchPlan
            .SelectPending([completed, pending, failed, changed]).Select(row => row.PeerId));
    }

    [Theory]
    [InlineData(0, 0, false, SynchronizationScreenPhase.CONFIGURATION)]
    [InlineData(2, 0, false, SynchronizationScreenPhase.CONFIGURATION)]
    [InlineData(2, 1, false, SynchronizationScreenPhase.CONFIGURATION)]
    [InlineData(2, 2, false, SynchronizationScreenPhase.HISTORY_RESULT)]
    [InlineData(2, 2, true, SynchronizationScreenPhase.SYNCHRONIZING)]
    [InlineData(2, 0, true, SynchronizationScreenPhase.SYNCHRONIZING)]
    public void ScreenPhase_ReflectsActualImportState(int total, int completed, bool busy,
        SynchronizationScreenPhase expected)
        => Assert.Equal(expected, SynchronizationScreenPhaseResolver.Resolve(total, completed, busy));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, -1)]
    [InlineData(2, 3)]
    public void ScreenPhase_RejectsInvalidCounters(int total, int complete)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            SynchronizationScreenPhaseResolver.Resolve(total, complete, false));

    private static ConfiguredChat Configured(long peer, ChatSynchronizationState state) => new()
    {
        TelegramUserId = 123, PeerId = peer, PeerType = "Channel",
        TelegramTitle = $"Пост чат {peer}", SocialNetwork = "Instagram",
        DisplayName = $"Пост чат {peer}", HistoryDays = 7,
        SynchronizationState = state, ImportedMessageCount = 5
    };

    private static PendingChatConnection Pending(long peer) => new()
    {
        TelegramUserId = 123, PeerId = peer, PeerType = "Channel",
        TelegramTitle = $"Пост чат {peer}", SocialNetwork = "Telegram", HistoryDays = 7
    };
}
