using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.GetChats;

public sealed class GetTelegramChatsHandler(ITelegramAuthenticationClient telegram)
    : IQueryHandler<IReadOnlyList<TelegramChatOption>, GetTelegramChatsQuery>
{
    public async Task<Result<IReadOnlyList<TelegramChatOption>, Error>> Handle(
        GetTelegramChatsQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var chats = await telegram.GetChatsAsync(cancellationToken);
            return Result.Success<IReadOnlyList<TelegramChatOption>, Error>(chats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TelegramAuthenticationException ex)
        {
            return Result.Failure<IReadOnlyList<TelegramChatOption>, Error>(
                new Error(ex.Code, ex.Message, ErrorType.SERVICE_UNAVAILABLE));
        }
    }
}
