using System.Globalization;
using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Save;

public sealed class SaveTelegramApiCredentialsHandler(
    ITelegramApiCredentialsStore store,
    ITelegramAuthenticationClient telegram)
    : ICommandHandler<bool, SaveTelegramApiCredentialsCommand>
{
    public async Task<Result<bool, Error>> Handle(
        SaveTelegramApiCredentialsCommand command, CancellationToken cancellationToken)
    {
        // Input format is enforced by the FluentValidation decorator.
        var apiId = int.Parse(command.ApiId, NumberStyles.None, CultureInfo.InvariantCulture);
        var previous = await store.LoadAsync(cancellationToken);
        if (TelegramApiCredentialsComparer.AreSame(previous, apiId, command.ApiHash))
        {
            // false means "nothing changed", NOT a failed command.
            return Result.Success<bool, Error>(false);
        }

        // A failed write must not destroy a working Telegram session.
        await store.SaveAsync(new TelegramApiCredentials(apiId, command.ApiHash), cancellationToken);
        try
        {
            // Once the new keys are committed, complete the local reset even if the
            // original UI operation is cancelled. Never use a client with stale keys.
            await telegram.ResetSessionAsync(CancellationToken.None);
        }
        catch (Exception resetException)
        {
            try
            {
                if (previous is null)
                    await store.DeleteAsync(CancellationToken.None);
                else
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
