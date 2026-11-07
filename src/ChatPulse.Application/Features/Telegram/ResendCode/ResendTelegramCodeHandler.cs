using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ResendCode;

public sealed class ResendTelegramCodeHandler(ITelegramAuthenticationClient telegram)
    : ICommandHandler<TelegramLoginProgress, ResendTelegramCodeCommand>
{
    public async Task<Result<TelegramLoginProgress, Error>> Handle(
        ResendTelegramCodeCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var progress = await telegram.ResendCodeAsync(cancellationToken);
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
