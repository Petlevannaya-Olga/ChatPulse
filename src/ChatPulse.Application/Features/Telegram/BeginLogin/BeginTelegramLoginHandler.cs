using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.BeginLogin;

public sealed class BeginTelegramLoginHandler(ITelegramAuthenticationClient telegram,
    ITelegramOperationGate operationGate)
    : ICommandHandler<TelegramLoginProgress, BeginTelegramLoginCommand>
{
    public async Task<Result<TelegramLoginProgress, Error>> Handle(
        BeginTelegramLoginCommand command, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken);
        // Входные данные проверяет только FluentValidation через декоратор.
        try
        {
            var progress = await telegram.BeginLoginAsync(command.PhoneNumber, cancellationToken);
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
