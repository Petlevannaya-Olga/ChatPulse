using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ContinueLogin;

public sealed record ContinueTelegramLoginCommand(string Value) : ICommandValidation;
