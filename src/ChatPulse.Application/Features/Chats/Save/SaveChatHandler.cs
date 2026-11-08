using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Chats.Save;

public sealed class SaveChatHandler(IChatConfigurationStore store)
    : ICommandHandler<bool, SaveChatCommand>
{
    public async Task<Result<bool, Error>> Handle(
        SaveChatCommand command, CancellationToken cancellationToken)
    {
        // Валидацию выполняет CommandValidationDecorator.
        await store.SaveAsync(
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
