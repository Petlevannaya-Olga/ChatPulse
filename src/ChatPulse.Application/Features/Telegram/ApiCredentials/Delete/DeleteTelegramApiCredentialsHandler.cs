using CSharpFunctionalExtensions;
using ChatPulse.Application.Features.Telegram;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Delete;

public sealed class DeleteTelegramApiCredentialsHandler(
    ITelegramAuthenticationClient telegram,
    ITelegramApiCredentialsStore store,
    ITelegramOperationGate operationGate,
    ITelegramCredentialChangeJournal journal)
    : ICommandHandler<bool, DeleteTelegramApiCredentialsCommand>
{
    public async Task<Result<bool, Error>> Handle(
        DeleteTelegramApiCredentialsCommand command, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken);
        await RecoverPendingTelegramCleanup.RunAsync(journal, telegram, cancellationToken);
        TelegramApiCredentials? previous;
        var previousUnreadable = false;
        try
        {
            previous = await store.LoadAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            // The user explicitly confirmed deleting unreadable/corrupted keys.
            // These cannot be rolled back, but leaving an unusable secret file in place
            // would make subsequent configuration impossible.
            previous = null;
            previousUnreadable = true;
        }

        await journal.MarkPendingAsync(cancellationToken);
        try
        {
            // A failed file deletion must not log the user out.
            await store.DeleteAsync(cancellationToken);
        }
        catch
        {
            await journal.ClearPendingAsync(CancellationToken.None);
            throw;
        }

        try
        {
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

            // Do not clear marker: credentials restored != session verified.
            throw new TelegramApiCredentialsUpdateException(!previousUnreadable, resetException);
        }

        try
        {
            await journal.ClearPendingAsync(CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            throw new TelegramApiCredentialsUpdateException(false, cleanupException, operationCommitted: true);
        }

        return Result.Success<bool, Error>(true);
    }
}
