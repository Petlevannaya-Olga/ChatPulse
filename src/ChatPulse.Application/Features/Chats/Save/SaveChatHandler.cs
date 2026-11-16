using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Contracts.Telegram;
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

        // Never trust the UI's cached classification or a caller-provided network.
        // Verify the current chat title and recent task evidence immediately before persistence.
        if (!ChatTitlePolicy.IsEligible(command.TelegramTitle))
            return Result.Failure<bool, Error>(new Error(
                "chat.title_not_eligible", "Название чата должно содержать слово «пост».", ErrorType.VALIDATION));

        ChatActivityAnalysis analysis;
        try
        {
            var snapshot = await telegram.GetChatsAsync(cancellationToken);
            var chat = snapshot.Chats.FirstOrDefault(item => item.PeerId == command.PeerId &&
                item.PeerType == command.PeerType);
            if (snapshot.TelegramUserId != actualUserId || chat is null ||
                !ChatTitlePolicy.IsEligible(chat.Title))
                return Result.Failure<bool, Error>(new Error(
                    "chat.not_eligible", "Чат недоступен или не соответствует требованиям.", ErrorType.VALIDATION));

            var messages = await telegram.GetRecentChatMessagesAsync(chat, 1, cancellationToken);
            analysis = ChatActivityAnalyzer.Analyze(messages.Select(item =>
                new ChatActivityMessage(item.MessageId, item.Text)));
            if (analysis.Classification == ChatActivityClassification.INSUFFICIENT_DATA)
            {
                messages = await telegram.GetRecentChatMessagesAsync(chat, 7, cancellationToken);
                analysis = ChatActivityAnalyzer.Analyze(messages.Select(item =>
                    new ChatActivityMessage(item.MessageId, item.Text)));
            }
        }
        catch (TelegramAuthenticationException exception)
        {
            return Result.Failure<bool, Error>(new Error(
                exception.Code, exception.Message, ErrorType.SERVICE_UNAVAILABLE));
        }

        var detectedNetwork = analysis.Network switch
        {
            ChatTaskNetwork.INSTAGRAM => "Instagram",
            ChatTaskNetwork.TELEGRAM => "Telegram",
            _ => null
        };
        if (analysis.Classification != ChatActivityClassification.POSSIBLE_ACTIVITY ||
            detectedNetwork != command.SocialNetwork)
            return Result.Failure<bool, Error>(new Error(
                "chat.analysis_not_passed",
                "Чат не прошёл проверку или определённая соцсеть изменилась.", ErrorType.VALIDATION));

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
