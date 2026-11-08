using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Chats.Save;

public sealed record SaveChatCommand(
    long PeerId,
    string PeerType,
    string TelegramTitle,
    string DisplayName,
    string SocialNetwork,
    DateOnly StartDate,
    long ExpectedTelegramUserId) : ICommandValidation;
