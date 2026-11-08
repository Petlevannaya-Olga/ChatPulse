using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.CancelLogin;

public sealed class CancelTelegramLoginHandler(
    ITelegramAuthenticationClient telegram,
    ITelegramOperationGate operationGate,
    ITelegramCredentialChangeJournal journal)
    : ICommandHandler<bool, CancelTelegramLoginCommand>
{
    public async Task<Result<bool, Error>> Handle(
        CancelTelegramLoginCommand command, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken);
        // Маркер гарантирует восстановление после аварии во время удаления сессии.
        // Preserve a marker left by a different interrupted operation: a failed
        // cancellation must not erase evidence of a pending credential rotation.
        var markerAlreadyPending = await journal.IsPendingAsync(cancellationToken);
        if (!markerAlreadyPending)
            await journal.MarkPendingAsync(cancellationToken);
        try
        {
            await telegram.CancelLoginAsync(CancellationToken.None);
        }
        catch (TelegramAuthenticationException ex) when (ex.Code == "telegram.login_not_pending")
        {
            // No reset was started. Keeping the marker would delete an unrelated,
            // potentially authorized session on the next application launch.
            if (!markerAlreadyPending)
                await journal.ClearPendingAsync(CancellationToken.None);
            return Result.Failure<bool, Error>(
                new Error(ex.Code, ex.Message, ErrorType.CONFLICT));
        }

        // For any other failure keep the marker: cancellation may have already
        // invalidated the client before the session file could be deleted.
        await journal.ClearPendingAsync(CancellationToken.None);
        return Result.Success<bool, Error>(true);
    }
}
