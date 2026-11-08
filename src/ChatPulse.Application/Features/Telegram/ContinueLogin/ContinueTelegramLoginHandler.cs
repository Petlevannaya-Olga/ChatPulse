using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ContinueLogin;

public sealed class ContinueTelegramLoginHandler(ITelegramAuthenticationClient telegram,
    ITelegramOperationGate operationGate)
    : ICommandHandler<TelegramLoginProgress, ContinueTelegramLoginCommand>
{
    public async Task<Result<TelegramLoginProgress, Error>> Handle(
        ContinueTelegramLoginCommand command, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken);
        try
        {
            var progress = await telegram.ContinueLoginAsync(command.Value, cancellationToken);
            return Result.Success<TelegramLoginProgress, Error>(progress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TelegramAuthenticationException ex)
        {
            return Result.Failure<TelegramLoginProgress, Error>(
                new Error(ex.Code, ex.Message, ErrorType.SERVICE_UNAVAILABLE));
        }
    }
}
