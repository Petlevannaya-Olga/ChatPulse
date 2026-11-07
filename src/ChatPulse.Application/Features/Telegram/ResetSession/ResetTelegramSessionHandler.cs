using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ResetSession;

public sealed class ResetTelegramSessionHandler(ITelegramAuthenticationClient telegram)
    : ICommandHandler<bool, ResetTelegramSessionCommand>
{
    public async Task<Result<bool, Error>> Handle(
        ResetTelegramSessionCommand command, CancellationToken cancellationToken)
    {
        // Это локальный сброс. Telegram auth.logOut здесь не вызывается.
        await telegram.ResetSessionAsync(cancellationToken);
        return Result.Success<bool, Error>(true);
    }
}
