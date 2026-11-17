namespace ChatPulse.Desktop.Views.Onboarding;

/// <summary>
/// Executes one explicit synchronization click against an immutable work plan.
/// The WPF view owns progress display; this coordinator owns ordering, dispatch
/// and reentrancy protection. Each row operation handles its own recoverable errors.
/// </summary>
public sealed class OnboardingSynchronizationBatchCoordinator
{
    private int running;

    public bool IsRunning => Volatile.Read(ref running) != 0;

    public async Task<bool> RunAsync(
        IEnumerable<SynchronizationChatRow> chats,
        Func<Task> savePeriods,
        Func<SynchronizationChatRow, Task> synchronize,
        Func<SynchronizationChatRow, Task> reload)
    {
        ArgumentNullException.ThrowIfNull(chats);
        ArgumentNullException.ThrowIfNull(savePeriods);
        ArgumentNullException.ThrowIfNull(synchronize);
        ArgumentNullException.ThrowIfNull(reload);

        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;
        try
        {
            // Capture both the selected rows and their operation kinds before
            // any await. A completed unchanged chat must never be imported again.
            var work = SynchronizationBatchPlan.SelectPending(chats)
                .Select(row => (Row: row, Reload: row.IsComplete && row.HasPeriodChange))
                .ToArray();
            if (work.Length == 0) return false;

            await savePeriods();
            foreach (var item in work)
            {
                if (item.Reload)
                    await reload(item.Row);
                else
                    await synchronize(item.Row);
            }
            return true;
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }
}
