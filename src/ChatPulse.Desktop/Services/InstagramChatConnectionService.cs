using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Adds an Instagram post-chat after the same 1-day/7-day automatic inspection
/// used in onboarding. A message without a supported Instagram post link does
/// not count as evidence. Settings are written only after a fresh verification.
/// </summary>
public sealed class InstagramChatConnectionService(
    ITelegramAuthenticationClient telegram,
    ISettingsService settings,
    ChatSynchronizationService synchronization)
{
    public async Task<IReadOnlyList<TelegramChatOption>> GetAvailableChatsAsync(
        CancellationToken cancellationToken = default)
    {
        var (owner, snapshot) = await GetCurrentSnapshotAsync(cancellationToken);
        var state = await settings.LoadAsync(cancellationToken);
        return snapshot.Chats
            .Where(chat => ChatTitlePolicy.IsEligible(chat.Title))
            .Where(chat => !state.Chats.Any(saved =>
                saved.TelegramUserId == owner &&
                saved.PeerId == chat.PeerId && saved.PeerType == chat.PeerType &&
                saved.SynchronizationState == ChatSynchronizationState.COMPLETED))
            .OrderBy(chat => chat.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<InstagramChatConnectionAnalysis> AnalyzeAsync(
        TelegramChatOption selected, CancellationToken cancellationToken = default)
    {
        var (_, snapshot) = await GetCurrentSnapshotAsync(cancellationToken);
        var chat = snapshot.Chats.FirstOrDefault(item => item.PeerId == selected.PeerId &&
            item.PeerType == selected.PeerType && ChatTitlePolicy.IsEligible(item.Title));
        if (chat is null)
            throw new InvalidOperationException("Чат недоступен или его название не содержит слово «пост».");

        var recent = await telegram.GetRecentChatMessagesAsync(chat, 1, cancellationToken);
        var analysis = Analyze(recent);
        if (analysis.Classification == ChatActivityClassification.INSUFFICIENT_DATA &&
            !InstagramChatConnectionAnalysis.IsInstagramEvidence(analysis))
        {
            recent = await telegram.GetRecentChatMessagesAsync(chat, 7, cancellationToken);
            analysis = Analyze(recent);
            return new InstagramChatConnectionAnalysis(analysis, 7, recent.Count);
        }
        return new InstagramChatConnectionAnalysis(analysis, 1, recent.Count);
    }

    public async Task<int> ConnectAsync(TelegramChatOption selected,
        CancellationToken cancellationToken = default)
    {
        var (owner, _) = await GetCurrentSnapshotAsync(cancellationToken);
        var state = await settings.LoadAsync(cancellationToken);
        if (state.Chats.Any(chat => chat.TelegramUserId == owner &&
            chat.PeerId == selected.PeerId && chat.PeerType == selected.PeerType &&
            chat.SynchronizationState == ChatSynchronizationState.COMPLETED))
            throw new InvalidOperationException("Этот чат уже подключён.");

        // Do not trust the previous preview: recheck Telegram before persisting.
        var analysis = await AnalyzeAsync(selected, cancellationToken);
        if (!analysis.IsInstagram)
            throw new InvalidOperationException(analysis.RejectionReason);
        var (currentOwner, snapshot) = await GetCurrentSnapshotAsync(cancellationToken);
        if (currentOwner != owner)
            throw new InvalidOperationException("Telegram-аккаунт изменился. Обновите список чатов.");
        var actual = snapshot.Chats.FirstOrDefault(chat => chat.PeerId == selected.PeerId &&
            chat.PeerType == selected.PeerType && ChatTitlePolicy.IsEligible(chat.Title));
        if (actual is null)
            throw new InvalidOperationException("Выбранный чат больше не доступен в Telegram.");

        var configured = new ConfiguredChat
        {
            TelegramUserId = owner,
            PeerId = actual.PeerId,
            PeerType = actual.PeerType,
            TelegramTitle = actual.Title,
            DisplayName = actual.Title,
            SocialNetwork = "Instagram",
            StartDate = DateOnly.FromDateTime(DateTime.Today),
            HistoryDays = 7,
            ParticipantRuleset = ChatParticipantHistoryRuleSelection.CHAT_14_RULESET
        };
        await settings.SaveChatAsync(configured, cancellationToken);
        // Initial task import remains independent of participant-list synchronization.
        return await synchronization.SynchronizeAsync(configured, cancellationToken: cancellationToken);
    }

    private static ChatActivityAnalysis Analyze(IReadOnlyList<TelegramChatMessage> messages) =>
        ChatActivityAnalyzer.Analyze(messages.Select(message =>
            new ChatActivityMessage(message.MessageId, message.Text)));

    private async Task<(long Owner, TelegramChatsSnapshot Snapshot)> GetCurrentSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var owner = await telegram.GetAuthorizedUserIdAsync(cancellationToken);
        if (owner <= 0) throw new InvalidOperationException("Telegram не авторизован.");
        var snapshot = await telegram.GetChatsAsync(cancellationToken);
        if (snapshot.TelegramUserId != owner)
            throw new InvalidOperationException("Telegram-аккаунт изменился. Обновите список чатов.");
        return (owner, snapshot);
    }
}

public sealed record InstagramChatConnectionAnalysis(
    ChatActivityAnalysis Analysis, int DaysScanned, int MessagesScanned)
{
    public bool IsInstagram => IsInstagramEvidence(Analysis);

    public static bool IsInstagramEvidence(ChatActivityAnalysis analysis) =>
        analysis.Messages.Any(message => message.Network == ChatTaskNetwork.INSTAGRAM &&
                                         message.PostLinks.Count > 0) &&
        analysis.Messages.All(message => message.PostLinks.Count == 0 ||
                                         message.Network == ChatTaskNetwork.INSTAGRAM);

    public string RejectionReason => Analysis.Classification == ChatActivityClassification.CONFLICTING_NETWORKS ||
                                     Analysis.Messages.Any(message => message.PostLinks.Count > 0 &&
                                         message.Network != ChatTaskNetwork.INSTAGRAM)
        ? "Обнаружены ссылки разных соцсетей. Автоматически определить Instagram не удалось."
        : "Не найдены сообщения с Instagram-ссылками на публикации " +
          "за последние семь дней. Сообщения без подходящих ссылок пропущены.";

    public string Description => IsInstagram
        ? $"Определён Instagram. Сообщений с публикациями: {Analysis.Messages.Count(message => message.PostLinks.Count > 0)}. Проверено дней: {DaysScanned}."
        : RejectionReason;
}
