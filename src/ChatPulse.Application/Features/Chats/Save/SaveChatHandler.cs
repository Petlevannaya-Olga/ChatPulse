using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Chats.Save;

public sealed class SaveChatHandler(
    IChatConfigurationStore store,
    ITelegramAuthenticationClient telegram,
    ITelegramOperationGate operationGate)
    : ICommandHandler<bool, SaveChatCommand>
{
    public async Task<Result<bool, Error>> Handle(
        SaveChatCommand command, CancellationToken cancellationToken)
    {
        // Валидацию выполняет CommandValidationDecorator. Блокировка защищает
        // проверку текущего Telegram user ID и запись от одновременного сброса сессии.
        using var operation = await operationGate.EnterAsync(cancellationToken);
        long actualUserId;
        try
        {
            actualUserId = await telegram.GetAuthorizedUserIdAsync(cancellationToken);
        }
        catch (TelegramAuthenticationException ex)
        {
            return Result.Failure<bool, Error>(
                new Error(ex.Code, ex.Message, ErrorType.SERVICE_UNAVAILABLE));
        }

        if (actualUserId != command.ExpectedTelegramUserId)
        {
            return Result.Failure<bool, Error>(new Error(
                "telegram.account_changed",
                "Telegram-аккаунт изменился. Обновите список чатов и выберите чат заново.",
                ErrorType.VALIDATION));
        }

        await store.SaveAsync(
            actualUserId,
            command.PeerId,
            command.PeerType,
            command.TelegramTitle,
            command.DisplayName.Trim(),
            command.SocialNetwork,
            command.StartDate,
            cancellationToken);
        return Result.Success<bool, Error>(true);
    }
}
