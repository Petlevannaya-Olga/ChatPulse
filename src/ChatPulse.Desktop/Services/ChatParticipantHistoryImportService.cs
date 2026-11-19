using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Loads all accessible Telegram list candidates, parses each in publication order,
/// and atomically rebuilds the isolated history tables. It does not touch the
/// live subscriptions register or the task synchronization cursor.
/// This service is not yet wired to any UI command.
/// </summary>
public sealed class ChatParticipantHistoryImportService(
    ITelegramParticipantListHistoryReader historyReader,
    ChatParticipantHistoryStore historyStore,
    IChatParticipantHistoryParser parser)
{
    public async Task<ParticipantHistoryRebuildResult> RebuildAsync(
        ConfiguredChat chat,
        Func<string, CancellationToken, Task<bool?>>? profileOpens = null,
        CancellationToken cancellationToken = default,
        Action<int, int, int>? onPageScanned = null,
        Action<int, int>? onCandidateProcessed = null, Action? onSaving = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (chat.TelegramUserId <= 0 || chat.PeerId == 0 ||
            chat.PeerType is not ("Chat" or "Channel"))
            throw new ArgumentException("Не выбран корректный Telegram-чат.", nameof(chat));

        var telegramChat = new TelegramChatOption(chat.PeerId, chat.PeerType,
            chat.TelegramTitle, chat.PeerType == "Chat" ? "Группа" : "Канал");
        var messages = await historyReader.FindAllMatchingMessagesAsync(
            telegramChat, parser.IsCandidate, cancellationToken, onPageScanned);
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Count == 0)
            throw new InvalidOperationException(
                "За доступный период Telegram не найдено ни одного полного списка участниц. " +
                "Существующий реестр не изменён.");

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ParticipantHistoryCandidate>(messages.Count);
        var processed = 0;
        foreach (var message in messages.OrderBy(item => item.MessageId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parse = await parser.ParseAsync(message.Text, message.SentAtUtc, known,
                profileOpens, cancellationToken);
            if (parse.IsSuccess)
            {
                foreach (var entry in parse.Entries!)
                {
                    // Normalized canonical URLs are returned by the chat parser.
                    var uri = new Uri(entry.SubscriptionProfileUrl);
                    known.Add(uri.AbsolutePath.Trim('/'));
                }
            }
            candidates.Add(new ParticipantHistoryCandidate(message.MessageId,
                message.SentAtUtc, message.Text ?? string.Empty, parse.Entries, parse.Error));
            onCandidateProcessed?.Invoke(++processed, messages.Count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        onSaving?.Invoke();
        return await historyStore.RebuildAsync(chat, candidates, parser.Rule, cancellationToken);
    }
}
