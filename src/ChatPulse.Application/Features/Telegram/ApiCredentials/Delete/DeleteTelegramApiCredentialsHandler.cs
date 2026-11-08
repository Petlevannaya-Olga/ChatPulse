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
        var previous = await store.LoadAsync(cancellationToken);

        // An unsuccessful secret-file deletion must not log the user out.
        await store.DeleteAsync(cancellationToken);
        try
        {
            // Do not permit cancellation between deletion and the required reset.
            await telegram.ResetSessionAsync(CancellationToken.None);
        }
        catch (Exception resetException)
        {
            try
            {
                if (previous is not null)
                    await store.SaveAsync(previous, CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                throw new TelegramApiCredentialsUpdateException(false,
                    new AggregateException(resetException, rollbackException));
            }

            throw new TelegramApiCredentialsUpdateException(true, resetException);
        }

        return Result.Success<bool, Error>(true);
    }
}
