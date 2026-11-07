using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Delete;

public sealed class DeleteTelegramApiCredentialsHandler(
    ITelegramAuthenticationClient telegram,
    ITelegramApiCredentialsStore store)
    : ICommandHandler<bool, DeleteTelegramApiCredentialsCommand>
{
    public async Task<Result<bool, Error>> Handle(
        DeleteTelegramApiCredentialsCommand command, CancellationToken cancellationToken)
    {
        // A local session must never remain usable with removed API credentials.
        await telegram.ResetSessionAsync(cancellationToken);
        await store.DeleteAsync(cancellationToken);
        return Result.Success<bool, Error>(true);
    }
}
