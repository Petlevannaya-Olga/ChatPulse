using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Application.Services;

/// <summary>
/// Searches Telegram history from newest to oldest and stops at the first matching message.
/// It does not modify the chat history or read markers.
/// </summary>
public interface ITelegramLatestMatchingMessageReader
{
    Task<TelegramChatMessage?> FindLatestMatchingMessageAsync(
        TelegramChatOption chat, Func<string?, bool> matches,
        CancellationToken cancellationToken);
}
