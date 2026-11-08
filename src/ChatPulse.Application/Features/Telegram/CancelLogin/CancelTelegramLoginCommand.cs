using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.CancelLogin;

/// <summary>Остановить незавершённый вход без удаления Telegram API-ключей.</summary>
public sealed record CancelTelegramLoginCommand : ICommand;
