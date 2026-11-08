using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ResetSession;

public sealed class ResetTelegramSessionHandler(ITelegramAuthenticationClient telegram,
    ITelegramOperationGate operationGate,
    ITelegramCredentialChangeJournal journal)
    : ICommandHandler<bool, ResetTelegramSessionCommand>
{
    public async Task<Result<bool, Error>> Handle(
        ResetTelegramSessionCommand command, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken);
        // The marker also makes a stand-alone reset crash-safe. No server logOut.
        await journal.MarkPendingAsync(cancellationToken);
        await telegram.ResetSessionAsync(CancellationToken.None);
        await journal.ClearPendingAsync(CancellationToken.None);
        return Result.Success<bool, Error>(true);
    }
}
