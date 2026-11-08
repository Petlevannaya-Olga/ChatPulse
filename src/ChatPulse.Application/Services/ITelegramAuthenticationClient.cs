using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Application.Services;

/// <summary>Одна Telegram-сессия для всех чатов пользователя.</summary>
public interface ITelegramAuthenticationClient
{
    Task<TelegramLoginProgress> RestoreSessionAsync(CancellationToken cancellationToken);
    Task<TelegramLoginProgress> BeginLoginAsync(string phoneNumber, CancellationToken cancellationToken);
    Task<TelegramLoginProgress> ContinueLoginAsync(string input, CancellationToken cancellationToken);
    Task<TelegramLoginProgress> ResendCodeAsync(CancellationToken cancellationToken);
    /// <summary>Отменяет незавершённый вход, сохраняя API-ключи и настройки чатов.</summary>
    Task CancelLoginAsync(CancellationToken cancellationToken);
    Task ResetSessionAsync(CancellationToken cancellationToken);
    Task<TelegramChatsSnapshot> GetChatsAsync(CancellationToken cancellationToken);
    /// <summary>Возвращает ID уже проверенной авторизованной сессии без нового запроса к Telegram.</summary>
    Task<long> GetAuthorizedUserIdAsync(CancellationToken cancellationToken);
}
