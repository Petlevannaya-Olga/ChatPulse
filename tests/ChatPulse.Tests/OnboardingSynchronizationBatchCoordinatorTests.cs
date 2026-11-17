using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class OnboardingSynchronizationBatchCoordinatorTests
{
    [Fact]
    public async Task Start_ProcessesOnlyPendingAndChangedRows_InStableOrder()
    {
        var existing = Row(101, ChatSynchronizationState.COMPLETED);
        var changed = Row(202, ChatSynchronizationState.COMPLETED);
        changed.Days = 14;
        var pending = Row(303, ChatSynchronizationState.PENDING);
        var calls = new List<string>();
        var coordinator = new OnboardingSynchronizationBatchCoordinator();

        var started = await coordinator.RunAsync([existing, changed, pending],
            () =>
            {
                calls.Add("save");
                return Task.CompletedTask;
            },
            row =>
            {
                calls.Add($"import:{row.PeerId}");
                row.SetCompleted(10);
                return Task.CompletedTask;
            },
            row =>
            {
                calls.Add($"reload:{row.PeerId}");
                row.SetCompleted(20);
                return Task.CompletedTask;
            });

        Assert.True(started);
        Assert.Equal(new[] { "save", "reload:202", "import:303" }, calls);
        Assert.False(coordinator.IsRunning);
        Assert.False(changed.HasPeriodChange);
        Assert.False(pending.NeedsSynchronization);
    }

    [Fact]
    public async Task NoChanges_DoesNotSaveOrContactTelegram()
    {
        var calls = 0;
        var completed = Row(101, ChatSynchronizationState.COMPLETED);

        var started = await new OnboardingSynchronizationBatchCoordinator().RunAsync([completed],
            () =>
            {
                calls++;
                return Task.CompletedTask;
            },
            _ => throw new InvalidOperationException("Unexpected import"),
            _ => throw new InvalidOperationException("Unexpected reload"));

        Assert.False(started);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ChangedPeriodRestoredToLoadedValue_DoesNotReload()
    {
        var completed = Row(101, ChatSynchronizationState.COMPLETED);
        completed.Days = 16;
        completed.Days = 7;

        var started = await new OnboardingSynchronizationBatchCoordinator().RunAsync([completed],
            () => throw new InvalidOperationException("Unexpected save"),
            _ => throw new InvalidOperationException("Unexpected import"),
            _ => throw new InvalidOperationException("Unexpected reload"));

        Assert.False(started);
    }

    [Fact]
    public async Task SecondClickWhileSaving_IsIgnoredWithoutDuplicatingWork()
    {
        var pending = Row(101, ChatSynchronizationState.PENDING);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var importCount = 0;
        var saveCount = 0;
        var coordinator = new OnboardingSynchronizationBatchCoordinator();
        async Task Save()
        {
            saveCount++;
            entered.TrySetResult(true);
            await release.Task;
        }
        Task Import(SynchronizationChatRow row)
        {
            importCount++;
            row.SetCompleted(1);
            return Task.CompletedTask;
        }

        var first = coordinator.RunAsync([pending], Save, Import, _ => Task.CompletedTask);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.IsRunning);

        var second = await coordinator.RunAsync([pending], Save, Import, _ => Task.CompletedTask);
        Assert.False(second);
        Assert.Equal(1, saveCount);
        Assert.Equal(0, importCount);

        release.SetResult(true);
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, importCount);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task FailedPeriodSave_PreventsImports_AndAllowsRetry()
    {
        var row = Row(101, ChatSynchronizationState.PENDING);
        var imports = 0;
        var coordinator = new OnboardingSynchronizationBatchCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync([row],
            () => throw new InvalidOperationException("Settings unavailable"),
            _ =>
            {
                imports++;
                return Task.CompletedTask;
            }, _ => Task.CompletedTask));

        Assert.Equal(0, imports);
        Assert.False(coordinator.IsRunning);
        var started = await coordinator.RunAsync([row], () => Task.CompletedTask,
            chat =>
            {
                imports++;
                chat.SetCompleted(5);
                return Task.CompletedTask;
            }, _ => Task.CompletedTask);
        Assert.True(started);
        Assert.Equal(1, imports);
    }

    [Fact]
    public async Task ImportFailure_MarkedOnRow_DoesNotBlockFollowingChat()
    {
        var first = Row(101, ChatSynchronizationState.PENDING);
        var second = Row(202, ChatSynchronizationState.PENDING);
        var attempted = new List<long>();

        var started = await new OnboardingSynchronizationBatchCoordinator().RunAsync([first, second],
            () => Task.CompletedTask,
            row =>
            {
                attempted.Add(row.PeerId);
                if (row.PeerId == 101) row.SetFailed("Disconnected");
                else row.SetCompleted(5);
                return Task.CompletedTask;
            }, _ => Task.CompletedTask);

        Assert.True(started);
        Assert.Equal(new long[] { 101, 202 }, attempted);
        Assert.True(first.NeedsSynchronization);
        Assert.False(second.NeedsSynchronization);
    }

    [Fact]
    public async Task RetryAfterFailure_DoesNotRepeatSuccessfulChats()
    {
        var first = Row(101, ChatSynchronizationState.FAILED);
        var successful = Row(202, ChatSynchronizationState.COMPLETED);
        var attempted = new List<long>();

        var started = await new OnboardingSynchronizationBatchCoordinator().RunAsync([first, successful],
            () => Task.CompletedTask,
            row =>
            {
                attempted.Add(row.PeerId);
                row.SetCompleted(8);
                return Task.CompletedTask;
            }, _ => Task.CompletedTask);

        Assert.True(started);
        Assert.Equal(new long[] { 101 }, attempted);
        Assert.False(first.NeedsSynchronization);
    }

    [Fact]
    public async Task ReloadFailure_PreservesOriginalStateForNextAttempt()
    {
        var original = Row(101, ChatSynchronizationState.COMPLETED);
        original.Days = 23;
        var attempts = 0;
        var coordinator = new OnboardingSynchronizationBatchCoordinator();
        Task Reload(SynchronizationChatRow row)
        {
            attempts++;
            if (attempts == 1) row.SetReloadFailed("Telegram unavailable");
            else row.SetCompleted(12);
            return Task.CompletedTask;
        }

        Assert.True(await coordinator.RunAsync([original], () => Task.CompletedTask,
            _ => throw new InvalidOperationException("Not an import"), Reload));
        Assert.True(original.IsComplete);
        Assert.True(original.HasPeriodChange);

        Assert.True(await coordinator.RunAsync([original], () => Task.CompletedTask,
            _ => throw new InvalidOperationException("Not an import"), Reload));
        Assert.Equal(2, attempts);
        Assert.True(original.IsComplete);
        Assert.False(original.HasPeriodChange);
    }

    [Fact]
    public async Task WorkSnapshot_IgnoresNewChangesMadeAfterSavingStarted()
    {
        var initiallyPending = Row(101, ChatSynchronizationState.PENDING);
        var completed = Row(202, ChatSynchronizationState.COMPLETED);
        var attempted = new List<long>();

        var started = await new OnboardingSynchronizationBatchCoordinator().RunAsync(
            [initiallyPending, completed],
            () =>
            {
                completed.Days = 15;
                return Task.CompletedTask;
            },
            row =>
            {
                attempted.Add(row.PeerId);
                row.SetCompleted(1);
                return Task.CompletedTask;
            },
            row =>
            {
                attempted.Add(row.PeerId);
                row.SetCompleted(1);
                return Task.CompletedTask;
            });

        Assert.True(started);
        Assert.Equal(new long[] { 101 }, attempted);
        Assert.True(completed.HasPeriodChange);
    }

    [Fact]
    public async Task UnexpectedOperationException_ReleasesBatchLockForRetry()
    {
        var row = Row(101, ChatSynchronizationState.PENDING);
        var coordinator = new OnboardingSynchronizationBatchCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync([row],
            () => Task.CompletedTask,
            _ => throw new InvalidOperationException("Unexpected service failure"),
            _ => Task.CompletedTask));

        Assert.False(coordinator.IsRunning);
        var retry = await coordinator.RunAsync([row], () => Task.CompletedTask,
            item =>
            {
                item.SetCompleted(1);
                return Task.CompletedTask;
            }, _ => Task.CompletedTask);
        Assert.True(retry);
        Assert.False(row.NeedsSynchronization);
    }

    private static SynchronizationChatRow Row(long id, ChatSynchronizationState state)
        => new(new ConfiguredChat
        {
            TelegramUserId = 123, PeerId = id, PeerType = "Channel",
            TelegramTitle = $"Пост чат {id}", DisplayName = $"Пост чат {id}",
            SocialNetwork = "Instagram", HistoryDays = 7, SynchronizationState = state
        });
}
