namespace ChatPulse.Application.Services;

/// <summary>Serializes Telegram operations with changes to API credentials.</summary>
public interface ITelegramOperationGate
{
    Task<IDisposable> EnterAsync(CancellationToken cancellationToken);
}
