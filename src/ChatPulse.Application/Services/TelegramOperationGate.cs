namespace ChatPulse.Application.Services;

/// <summary>
/// A single gate shared by all Telegram command and query handlers.
/// During shutdown active leases may still be released; queued requests are rejected.
/// </summary>
public sealed class TelegramOperationGate : ITelegramOperationGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            await _semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(TelegramOperationGate));
        }

        // Dispose can race with successful WaitAsync. Do not grant a new lease
        // after shutdown, and release the semaphore if we just acquired it.
        if (Volatile.Read(ref _disposed) != 0)
        {
            _semaphore.Release();
            throw new ObjectDisposedException(nameof(TelegramOperationGate));
        }

        return new Lease(_semaphore);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(TelegramOperationGate));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _shutdown.Cancel();

        // Intentionally do not dispose _semaphore or _shutdown here.
        // An in-flight handler may still own a lease, and waiters are just
        // processing cancellation. Both synchronization objects are private
        // and will be collected with this application-scoped singleton.
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _owned = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _owned, null)?.Release();
    }
}
