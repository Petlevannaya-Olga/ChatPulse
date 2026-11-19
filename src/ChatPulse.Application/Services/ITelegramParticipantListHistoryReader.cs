using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Application.Services;

/// <summary>
/// Reads the entire history available to the authorized Telegram account for one chat,
/// retaining only messages selected by a cheap *candidate* predicate.
/// Never moves the task-import cursor or marks Telegram messages as read.
/// A partial read must throw, not return a seemingly complete history.
/// </summary>
public interface ITelegramParticipantListHistoryReader
{
    Task<IReadOnlyList<TelegramChatMessage>> FindAllMatchingMessagesAsync(
        TelegramChatOption chat,
        Func<string?, bool> looksLikeParticipantList,
        CancellationToken cancellationToken,
        Action<int, int, int>? onPageScanned = null);
}
