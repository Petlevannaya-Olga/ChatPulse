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

    /// <summary>
    /// Reads every candidate newer than the last committed list. A transport may
    /// stop pagination at the boundary; default fallback performs a full scan.
    /// Never return a partial result when Telegram reading fails.
    /// </summary>
    async Task<IReadOnlyList<TelegramChatMessage>> FindMatchingMessagesAfterAsync(
        TelegramChatOption chat, long afterMessageId,
        Func<string?, bool> looksLikeParticipantList,
        CancellationToken cancellationToken, Action<int, int, int>? onPageScanned = null)
    {
        if (afterMessageId < 0) throw new ArgumentOutOfRangeException(nameof(afterMessageId));
        var found = await FindAllMatchingMessagesAsync(chat, looksLikeParticipantList,
            cancellationToken, onPageScanned);
        return found.Where(item => item.MessageId > afterMessageId)
            .OrderBy(item => item.MessageId).ToArray();
    }
}
