using System.Globalization;
using CSharpFunctionalExtensions;
using ChatPulse.Application.Features.Telegram;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Save;

public sealed class SaveTelegramApiCredentialsHandler(
    ITelegramApiCredentialsStore store,
    ITelegramAuthenticationClient telegram,
    ITelegramOperationGate operationGate,
    ITelegramCredentialChangeJournal journal)
    : ICommandHandler<bool, SaveTelegramApiCredentialsCommand>
{
    public async Task<Result<bool, Error>> Handle(
        SaveTelegramApiCredentialsCommand command, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken);
        bool recoveredInterruptedChange;
        try
        {
            recoveredInterruptedChange = await RecoverPendingTelegramCleanup.RunAsync(journal, telegram, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception recoveryException)
        {
            // Recovery may already have invalidated the client or deleted its
            // file. Never report the previous session as still verified.
            throw new TelegramApiCredentialsUpdateException(false, recoveryException);
        }
        // Input format is enforced by the FluentValidation decorator.
        var apiId = int.Parse(command.ApiId, NumberStyles.None, CultureInfo.InvariantCulture);
        var previous = await store.LoadAsync(cancellationToken);
        if (TelegramApiCredentialsComparer.AreSame(previous, apiId, command.ApiHash))
        {
            // A pending crash recovery may have invalidated the session even
            // though the API keys themselves are unchanged. Inform the UI.
            return Result.Success<bool, Error>(recoveredInterruptedChange);
        }

        // Durable marker is committed BEFORE modifying credentials. If the process
        // crashes after the write but before reset, the next login/restore will
        // discard the old session instead of trying it with different API keys.
        await journal.MarkPendingAsync(cancellationToken);
        try
        {
            // Atomic file replacement: if this throws, the previous keys remain.
            await store.SaveAsync(new TelegramApiCredentials(apiId, command.ApiHash), cancellationToken);
        }
        catch
        {
            await journal.ClearPendingAsync(CancellationToken.None);
            throw;
        }

        try
        {
            // After a successful key write, do not interrupt the mandatory reset.
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
                // Keep marker: next startup must clear a potentially stale session.
                throw new TelegramApiCredentialsUpdateException(false,
                    new AggregateException(resetException, rollbackException));
            }

            // Marker deliberately stays until a successful local session cleanup.
            throw new TelegramApiCredentialsUpdateException(true, resetException);
        }

        try
        {
            await journal.ClearPendingAsync(CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            // No rollback: session is already cleared and new keys are valid.
            throw new TelegramApiCredentialsUpdateException(false, cleanupException, operationCommitted: true);
        }

        return Result.Success<bool, Error>(true);
    }
}
