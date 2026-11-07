using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Save;

public sealed record SaveTelegramApiCredentialsCommand(string ApiId, string ApiHash)
    : ICommandValidation;
