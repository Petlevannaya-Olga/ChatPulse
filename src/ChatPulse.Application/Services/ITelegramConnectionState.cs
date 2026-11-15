namespace ChatPulse.Application.Services;

/// <summary>
/// Read-only connectivity information for UI operations. This is not an
/// authorization state: losing the network never deletes a Telegram session.
/// </summary>
public interface ITelegramConnectionState
{
    bool IsNetworkAvailable { get; }
    bool CanUseTelegram { get; }
    event Action? StateChanged;
}
