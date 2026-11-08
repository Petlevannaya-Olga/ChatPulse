namespace ChatPulse.Application.Services;

/// <summary>
/// Durable marker: after an interrupted API key update the old local session must
/// be discarded before any WTelegram.Client is created or reused.
/// Contains no credentials or other account information.
/// </summary>
public interface ITelegramCredentialChangeJournal
{
    Task MarkPendingAsync(CancellationToken cancellationToken);
    Task<bool> IsPendingAsync(CancellationToken cancellationToken);
    Task ClearPendingAsync(CancellationToken cancellationToken);
}
