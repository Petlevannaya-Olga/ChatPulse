using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Application.Services;

/// <summary>Одна Telegram-сессия для всех чатов пользователя.</summary>
public interface ITelegramAuthenticationClient
{
    Task<TelegramLoginProgress> RestoreSessionAsync(CancellationToken cancellationToken);
    Task<TelegramLoginProgress> BeginLoginAsync(string phoneNumber, CancellationToken cancellationToken);
    Task<TelegramLoginProgress> ContinueLoginAsync(string input, CancellationToken cancellationToken);
    Task<TelegramLoginProgress> ResendCodeAsync(CancellationToken cancellationToken);
    Task ResetSessionAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TelegramChatOption>> GetChatsAsync(CancellationToken cancellationToken);
}
