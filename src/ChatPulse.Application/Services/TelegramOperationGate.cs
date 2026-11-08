namespace ChatPulse.Application.Services;

/// <summary>A single gate shared by all Telegram command and query handlers.</summary>
public sealed class TelegramOperationGate : ITelegramOperationGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Lease(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _owned = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _owned, null)?.Release();
    }
}
