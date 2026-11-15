using ChatPulse.Application.Services;

namespace ChatPulse.Tests;

internal sealed class FakeTelegramConnectionState : ITelegramConnectionState
{
    public bool IsNetworkAvailable { get; private set; } = true;
    public bool CanUseTelegram { get; private set; } = true;
    public event Action? StateChanged;

    public void SetState(bool networkAvailable, bool canUseTelegram)
    {
        IsNetworkAvailable = networkAvailable;
        CanUseTelegram = canUseTelegram;
        StateChanged?.Invoke();
    }
}
