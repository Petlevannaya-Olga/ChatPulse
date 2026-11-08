using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.GetChats;

public sealed class GetTelegramChatsHandler(ITelegramAuthenticationClient telegram,
    ITelegramOperationGate operationGate)
    : IQueryHandler<TelegramChatsSnapshot, GetTelegramChatsQuery>
{
    public async Task<Result<TelegramChatsSnapshot, Error>> Handle(
        GetTelegramChatsQuery query, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken);
        try
        {
            var chats = await telegram.GetChatsAsync(cancellationToken);
            return Result.Success<TelegramChatsSnapshot, Error>(chats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TelegramAuthenticationException ex)
        {
            return Result.Failure<TelegramChatsSnapshot, Error>(
                new Error(ex.Code, ex.Message, ErrorType.SERVICE_UNAVAILABLE));
        }
    }
}
