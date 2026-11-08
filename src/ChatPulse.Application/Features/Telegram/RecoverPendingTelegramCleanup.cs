using ChatPulse.Application.Services;

namespace ChatPulse.Application.Features.Telegram;

/// <summary>
/// Recover a previous interrupted change before comparing or modifying credentials.
/// Without this, saving identical keys could bypass the marker and leave a stale session.
/// Must be called while holding ITelegramOperationGate.
/// </summary>
internal static class RecoverPendingTelegramCleanup
{
    internal static async Task RunAsync(
        ITelegramCredentialChangeJournal journal,
        ITelegramAuthenticationClient telegram,
        CancellationToken cancellationToken)
    {
        if (!await journal.IsPendingAsync(cancellationToken)) return;
        await telegram.ResetSessionAsync(CancellationToken.None);
        await journal.ClearPendingAsync(CancellationToken.None);
    }
}
