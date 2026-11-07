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
        // Формат проверяет только FluentValidation через декоратор.
        // При изменении ключей нельзя использовать старую Telegram-сессию.
        await telegram.ResetSessionAsync(cancellationToken);
        await store.SaveAsync(new TelegramApiCredentials(
            int.Parse(command.ApiId, NumberStyles.None, CultureInfo.InvariantCulture),
            command.ApiHash), cancellationToken);
        return Result.Success<bool, Error>(true);
    }
}
