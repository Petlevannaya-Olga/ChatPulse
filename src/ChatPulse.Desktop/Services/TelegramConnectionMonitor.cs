using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using ChatPulse.Application.Features.Telegram.RestoreSession;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Shared.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Application-wide connection indicator. The Windows network signal is monitored
/// continuously; Telegram is checked via the existing serialized CQRS operation.
/// No codes are requested and no saved session or API credentials are deleted.
/// </summary>
public sealed class TelegramConnectionMonitor : ITelegramConnectionState, INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan CHECK_INTERVAL = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ONLINE_RECHECK = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FAILED_RECHECK = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RECONNECT_SETTLE = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelegramAuthorizationEvents _authorizationEvents;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private Func<bool>? _canCheck;
    private CancellationTokenSource? _checkCancellation;
    private bool _active;
    private bool _networkAvailable;
    private bool _checking;
    private bool _retryAfterCurrentCheck;
    private bool _disposed;
    private readonly TelegramConnectionProbeGeneration _probeGeneration = new();
    private DateTimeOffset _nextCheckAt;
    private TelegramConnectionStatus _status = TelegramConnectionStatus.ONLINE;

    public TelegramConnectionMonitor(IServiceScopeFactory scopeFactory,
        TelegramAuthorizationEvents authorizationEvents)
    {
        _scopeFactory = scopeFactory;
        _authorizationEvents = authorizationEvents;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _networkAvailable = HasUsableNetworkRoute();
        _status = _networkAvailable ? TelegramConnectionStatus.ONLINE : TelegramConnectionStatus.OFFLINE;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = CHECK_INTERVAL
        };
        _timer.Tick += OnTick;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _authorizationEvents.AuthorizationLost += OnAuthorizationLost;
        _authorizationEvents.VerificationRequired += OnVerificationRequired;
        _timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? StateChanged;
    public event Action<TelegramLoginProgress>? SessionVerified;

    public TelegramConnectionStatus Status => _status;
    public bool IsNetworkAvailable => _networkAvailable;
    public bool CanUseTelegram => _networkAvailable && (!_active || _status == TelegramConnectionStatus.ONLINE);
    // The offline banner must work on every screen, including onboarding before login.
    // _active controls Telegram session probes only, not network notifications.
    public bool IsWarningVisible => _status != TelegramConnectionStatus.ONLINE;
    public bool CanRetry => _active && _networkAvailable && !_checking &&
                            _status == TelegramConnectionStatus.TELEGRAM_UNAVAILABLE;
    public string NoticeText => _status switch
    {
        TelegramConnectionStatus.OFFLINE => "Нет сетевого подключения. Telegram временно недоступен.",
        TelegramConnectionStatus.CHECKING => "Проверяем подключение к Telegram…",
        TelegramConnectionStatus.TELEGRAM_UNAVAILABLE => "Не удалось связаться с Telegram. Повторите проверку.",
        _ => string.Empty
    };

    public void StartMonitoring(Func<bool> canCheck)
    {
        if (_disposed) return;
        _canCheck = canCheck;
        if (_active) return;
        _active = true;
        _networkAvailable = HasUsableNetworkRoute();
        _nextCheckAt = DateTimeOffset.UtcNow + ONLINE_RECHECK;
        SetStatus(_networkAvailable ? TelegramConnectionStatus.ONLINE : TelegramConnectionStatus.OFFLINE);
        NotifyStateChanged();
    }

    public void StopMonitoring()
    {
        if (!_active) return;
        _active = false;
        _canCheck = null;
        _probeGeneration.Advance();
        _checkCancellation?.Cancel();
        _retryAfterCurrentCheck = false;
        _networkAvailable = HasUsableNetworkRoute();
        SetStatus(_networkAvailable ? TelegramConnectionStatus.ONLINE : TelegramConnectionStatus.OFFLINE);
        NotifyStateChanged();
    }

    /// <summary>Called when a Telegram request cannot confirm the old session.</summary>
    public void ReportVerificationRequired()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(ReportVerificationRequired));
            return;
        }

        if (!_active || _disposed) return;
        _networkAvailable = HasUsableNetworkRoute();
        _nextCheckAt = DateTimeOffset.UtcNow;
        SetStatus(_networkAvailable ? TelegramConnectionStatus.TELEGRAM_UNAVAILABLE :
            TelegramConnectionStatus.OFFLINE);
    }

    public Task RetryAsync()
    {
        // If another Telegram operation is busy, the timer will retry once it ends.
        _nextCheckAt = DateTimeOffset.UtcNow;
        return CheckTelegramAsync();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
        => QueueNetworkEvaluation();

    // AvailabilityChanged may not fire when a virtual adapter remains connected.
    private void OnNetworkAddressChanged(object? sender, EventArgs args)
        => QueueNetworkEvaluation();

    private void QueueNetworkEvaluation()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(EvaluateNetwork));
    }

    private void OnTick(object? sender, EventArgs args)
    {
        EvaluateNetwork();
        if (_active && _networkAvailable && !_checking && DateTimeOffset.UtcNow >= _nextCheckAt)
            _ = CheckTelegramAsync();
    }

    private void EvaluateNetwork()
    {
        if (_disposed) return;
        var available = HasUsableNetworkRoute();
        if (available == _networkAvailable) return;
        _networkAvailable = available;
        NotifyStateChanged();

        if (!available)
        {
            if (_active)
            {
                _probeGeneration.Advance();
                _checkCancellation?.Cancel();
            }
            // Report Wi-Fi loss even before Telegram authorization.
            SetStatus(TelegramConnectionStatus.OFFLINE);
        }
        else if (!_active)
        {
            // No authorized session: Windows network status is all we can verify.
            SetStatus(TelegramConnectionStatus.ONLINE);
        }
        else
        {
            _nextCheckAt = DateTimeOffset.UtcNow;
            SetStatus(TelegramConnectionStatus.CHECKING);
            if (_checking)
                _retryAfterCurrentCheck = true;
            else
                _ = CheckTelegramAsync();
        }
    }

    // NetworkInterface.GetIsNetworkAvailable() also counts host-only and other
    // virtual adapters. An active non-tunnel interface with a default gateway
    // is a better signal for local connectivity (not proof of Internet access).
    private static bool HasUsableNetworkRoute()
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                        NetworkInterfaceType.Tunnel)
                    continue;

                try
                {
                    if (networkInterface.GetIPProperties().GatewayAddresses.Any(gateway =>
                            !gateway.Address.Equals(IPAddress.Any) &&
                            !gateway.Address.Equals(IPAddress.IPv6Any)))
                        return true;
                }
                catch (NetworkInformationException)
                {
                    // A transient adapter reconfiguration must not break the timer.
                }
            }

            return false;
        }
        catch (NetworkInformationException)
        {
            // Network enumeration failed; preserve OS availability as a fallback.
            return NetworkInterface.GetIsNetworkAvailable();
        }
    }

    private async Task CheckTelegramAsync()
    {
        if (_disposed || !_active || !_networkAvailable || _checking ||
            (_canCheck is not null && !_canCheck())) return;

        _checking = true;
        var generation = _probeGeneration.Advance();
        var cancellation = new CancellationTokenSource();
        _checkCancellation = cancellation;
        // Routine background probes stay silent; show CHECKING only after an
        // outage or when the user explicitly retries.
        if (_status != TelegramConnectionStatus.ONLINE)
            SetStatus(TelegramConnectionStatus.CHECKING);

        try
        {
            await Task.Delay(RECONNECT_SETTLE, cancellation.Token);
            // The CQRS handler uses ITelegramOperationGate. No overlapping sign-in,
            // chat load or background connection verification is permitted.
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                IQueryHandler<TelegramLoginProgress, RestoreTelegramSessionQuery>>();
            var result = await handler.Handle(new RestoreTelegramSessionQuery(), cancellation.Token);

            if (!IsCurrent(generation)) return;
            if (result.IsSuccess && result.Value.IsAuthorized)
            {
                // A restored local user is not proof of Internet reachability.
                // Force a read-only Telegram RPC under the same shared gate used
                // for login and chat operations.
                using (var operation = await scope.ServiceProvider
                           .GetRequiredService<ITelegramOperationGate>().EnterAsync(cancellation.Token))
                {
                    await scope.ServiceProvider.GetRequiredService<ITelegramAuthenticationClient>()
                        .CheckConnectionAsync(cancellation.Token);
                }
                if (!IsCurrent(generation)) return;
                SetStatus(TelegramConnectionStatus.ONLINE);
                SessionVerified?.Invoke(result.Value);
            }
            else if (result.IsFailure && result.Error.Code is
                     "telegram.session_revoked" or "telegram.session_corrupted" or "telegram.session_unavailable")
            {
                _authorizationEvents.ReportAuthorizationLost();
                StopMonitoring();
            }
            else if (result.IsSuccess && !result.Value.IsAuthorized)
            {
                // The old session disappeared or is no longer authorized.
                _authorizationEvents.ReportAuthorizationLost();
                StopMonitoring();
            }
            else
            {
                SetStatus(TelegramConnectionStatus.TELEGRAM_UNAVAILABLE);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Network went offline, session was reset, or application is closing.
        }
        catch (Exception)
        {
            if (IsCurrent(generation)) SetStatus(TelegramConnectionStatus.TELEGRAM_UNAVAILABLE);
        }
        finally
        {
            if (ReferenceEquals(_checkCancellation, cancellation))
            {
                _checkCancellation = null;
                _checking = false;
                _nextCheckAt = _retryAfterCurrentCheck
                    ? DateTimeOffset.UtcNow
                    : DateTimeOffset.UtcNow +
                      (_status == TelegramConnectionStatus.ONLINE ? ONLINE_RECHECK : FAILED_RECHECK);
                _retryAfterCurrentCheck = false;
                NotifyStateChanged();
            }
            cancellation.Dispose();
        }
    }

    private bool IsCurrent(long generation) =>
        !_disposed && _active && _networkAvailable && _probeGeneration.IsCurrent(generation);

    private void OnVerificationRequired() => ReportVerificationRequired();

    private void OnAuthorizationLost()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) StopMonitoring();
        else _dispatcher.BeginInvoke(new Action(StopMonitoring));
    }

    private void SetStatus(TelegramConnectionStatus status)
    {
        if (_status == status) return;
        _status = status;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsWarningVisible));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(CanRetry));
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsNetworkAvailable));
        OnPropertyChanged(nameof(CanUseTelegram));
        OnPropertyChanged(nameof(IsWarningVisible));
        OnPropertyChanged(nameof(CanRetry));
        StateChanged?.Invoke();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _authorizationEvents.AuthorizationLost -= OnAuthorizationLost;
        _authorizationEvents.VerificationRequired -= OnVerificationRequired;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _checkCancellation?.Cancel();
    }
}

public enum TelegramConnectionStatus
{
    ONLINE,
    OFFLINE,
    CHECKING,
    TELEGRAM_UNAVAILABLE
}
