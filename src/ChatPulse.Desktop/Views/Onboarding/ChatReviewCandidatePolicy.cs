using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Views.Onboarding;

/// <summary>
/// Maps an immutable review snapshot to pending connections. Any mismatch between
/// reviewed rows and selected chats invalidates the entire result, not just a row.
/// </summary>
public static class ChatReviewCandidatePolicy
{
    public static bool AreSelectionsCurrent(IEnumerable<TelegramChatOption> selected,
        IEnumerable<TelegramChatOption> available)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(available);
        var current = available.Select(chat => (chat.PeerId, chat.PeerType, chat.Title)).ToHashSet();
        return selected.All(chat => current.Contains((chat.PeerId, chat.PeerType, chat.Title)));
    }

    public static PendingChatConnection[] CreateCandidates(long owner,
        IReadOnlyList<TelegramChatOption> selected, IReadOnlyList<ChatAnalysisResultRow> results)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(results);
        if (owner <= 0 || selected.Count == 0 || selected.Count > 10 ||
            selected.Count != results.Count) return [];

        var seen = new HashSet<(long PeerId, string PeerType)>();
        var candidates = new List<PendingChatConnection>();
        for (var i = 0; i < selected.Count; i++)
        {
            var chat = selected[i];
            var result = results[i];
            if (!string.Equals(chat.Title, result.Title, StringComparison.Ordinal) ||
                (result.PeerId.HasValue && result.PeerId.Value != chat.PeerId) ||
                (result.PeerType is not null &&
                 !string.Equals(result.PeerType, chat.PeerType, StringComparison.Ordinal)) ||
                !ChatTitlePolicy.IsEligible(chat.Title) ||
                !seen.Add((chat.PeerId, chat.PeerType)))
                return [];

            if (!result.CanConnect) continue;
            var network = result.VerifiedNetwork switch
            {
                ChatTaskNetwork.INSTAGRAM => "Instagram",
                ChatTaskNetwork.TELEGRAM => "Telegram",
                _ => null
            };
            if (network is null) continue;
            candidates.Add(new PendingChatConnection
            {
                TelegramUserId = owner,
                PeerId = chat.PeerId,
                PeerType = chat.PeerType,
                TelegramTitle = chat.Title,
                SocialNetwork = network,
                HistoryDays = 7
            });
        }
        return [.. candidates];
    }
}
