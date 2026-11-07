using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Application.Services;

/// <summary>Одна Telegram-сессия для всех чатов пользователя.</summary>
public interface ITelegramAuthenticationClient
{
    Task<TelegramLoginProgress> BeginLoginAsync(string phoneNumber, CancellationToken cancellationToken);
    Task<TelegramLoginProgress> ContinueLoginAsync(string input, CancellationToken cancellationToken);
    Task ResetSessionAsync(CancellationToken cancellationToken);
}
