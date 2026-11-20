using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>
/// A complete, cancellation-aware Telegram history traversal. The transport supplies
/// consecutive pages in descending message-ID order. An empty page means end of history.
/// Never return partial results after pagination becomes inconsistent.
/// </summary>
internal static class TelegramParticipantHistoryTraversal
{
    public static async Task<IReadOnlyList<TelegramChatMessage>> CollectAfterAsync(
        Func<int, CancellationToken, Task<IReadOnlyList<TelegramChatMessage>>> loadPage,
        Func<string?, bool> matches, long afterMessageId,
        CancellationToken cancellationToken, Action<int, int, int>? onPageScanned = null)
    {
        ArgumentNullException.ThrowIfNull(loadPage);
        ArgumentNullException.ThrowIfNull(matches);
        if (afterMessageId < 0) throw new ArgumentOutOfRangeException(nameof(afterMessageId));
        var found = new List<TelegramChatMessage>();
        var offset = 0;
        var pages = 0;
        var scanned = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messages = await loadPage(offset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (messages.Count == 0) return found.OrderBy(item => item.MessageId).ToArray();
            long previous = offset == 0 ? long.MaxValue : offset;
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message.MessageId <= 0 || message.MessageId >= previous)
                    throw new InvalidOperationException(
                        "Telegram вернул повторяющуюся или неправильно отсортированную историю. " +
                        "Синхронизация участниц не выполнена.");
                previous = message.MessageId;
                scanned++;
                if (message.MessageId <= afterMessageId)
                {
                    onPageScanned?.Invoke(++pages, scanned, found.Count);
                    return found.OrderBy(item => item.MessageId).ToArray();
                }
                if (matches(message.Text)) found.Add(message);
            }
            if (previous > int.MaxValue)
                throw new InvalidOperationException("Telegram message_id превышает допустимый диапазон.");
            offset = checked((int)previous);
            onPageScanned?.Invoke(++pages, scanned, found.Count);
        }
    }

    public static async Task<IReadOnlyList<TelegramChatMessage>> CollectAsync(
        Func<int, CancellationToken, Task<IReadOnlyList<TelegramChatMessage>>> loadPage,
        Func<string?, bool> matches,
        CancellationToken cancellationToken,
        Action<int, int, int>? onPageScanned = null)
    {
        ArgumentNullException.ThrowIfNull(loadPage);
        ArgumentNullException.ThrowIfNull(matches);

        var matching = new List<TelegramChatMessage>();
        var offsetId = 0;
        var pages = 0;
        var scanned = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messages = await loadPage(offsetId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (messages.Count == 0)
                return matching.OrderBy(item => item.MessageId).ToArray();

            long previous = offsetId == 0 ? long.MaxValue : offsetId;
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message.MessageId <= 0 || message.MessageId >= previous)
                    throw new InvalidOperationException(
                        "Telegram вернул повторяющуюся или неправильно отсортированную страницу истории. " +
                        "Полное обновление не выполнено.");
                previous = message.MessageId;
                scanned++;
                if (matches(message.Text)) matching.Add(message);
            }

            if (previous > int.MaxValue)
                throw new InvalidOperationException(
                    "Идентификатор сообщения Telegram превышает поддерживаемый диапазон. " +
                    "Полное обновление не выполнено.");
            offsetId = checked((int)previous);
            pages++;
            onPageScanned?.Invoke(pages, scanned, matching.Count);
        }
    }
}
