using System.Net.NetworkInformation;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>
/// Detects the OS reporting that all usable network interfaces became unavailable.
/// This is an early signal, not an internet-reachability check: routers and VPNs
/// may remain connected without working internet. The remote timeout still applies.
/// </summary>
internal sealed class TelegramNetworkAvailabilityMonitor : IDisposable
{
    private readonly CancellationTokenSource _unavailable = new();
    private int _disposed;

    public TelegramNetworkAvailabilityMonitor()
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        if (!NetworkInterface.GetIsNetworkAvailable())
            _unavailable.Cancel();
    }

    public CancellationToken UnavailableToken => _unavailable.Token;

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        if (args.IsAvailable || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _unavailable.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A late OS event can race with Dispose; the monitor is already gone.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _unavailable.Dispose();
    }
}
