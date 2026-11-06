using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.BeginLogin;

public sealed record BeginTelegramLoginCommand(string PhoneNumber) : ICommandValidation;
