using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Application.Services;

/// <summary>Explicit user-requested reaction to the ORIGINAL Telegram message.</summary>
public interface ITelegramTaskReactionSender
{
    Task SendHeartReactionAsync(TelegramChatOption chat, int messageId,
        CancellationToken cancellationToken);
}
