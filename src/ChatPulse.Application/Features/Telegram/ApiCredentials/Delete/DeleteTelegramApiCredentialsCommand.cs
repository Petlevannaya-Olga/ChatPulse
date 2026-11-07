using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Delete;

/// <summary>Remove local Telegram API credentials and their associated local session.</summary>
public sealed record DeleteTelegramApiCredentialsCommand : ICommand;
