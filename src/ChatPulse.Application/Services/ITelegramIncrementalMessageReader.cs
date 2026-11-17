using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Application.Services;

/// <summary>Loads all available messages strictly newer than a chat-local message ID.</summary>
public interface ITelegramIncrementalMessageReader
{
    Task<IReadOnlyList<TelegramChatMessage>> GetChatMessagesAfterAsync(
        TelegramChatOption chat, int messageId, CancellationToken cancellationToken);
}
