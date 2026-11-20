namespace ChatPulse.Desktop.Services;

/// <summary>
/// Compatibility rules for chats without an explicitly configured historical
/// parser. Only the chat-14 parser accepts malformed domains in penalty links.
/// </summary>
public sealed class StandardParticipantHistoryParser(bool instagramOnly)
    : IChatParticipantHistoryParser
{
    public ParticipantHistoryRule Rule { get; } = new(1,
        instagramOnly ? "StandardInstagramList" : "StandardParticipantList", "{}");

    public bool IsCandidate(string? text) => ParticipantListParser.LooksLikeParticipantList(text);

    public async Task<ChatParticipantHistoryParseResult> ParseAsync(string? text,
        DateTime? sentAtUtc, IReadOnlySet<string> previouslySeenUsernames,
        Func<string, CancellationToken, Task<bool?>>? profileOpens,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Existing parser uses a synchronous profile callback; run it off the UI
        // thread to avoid blocking the dialog while Instagram is being checked.
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Func<string, bool?>? check = profileOpens is null ? null :
                url => profileOpens(url, cancellationToken).GetAwaiter().GetResult();
            if (!ParticipantListParser.TryParse(text, out var result, out var error,
                    check, instagramOnly))
                return new ChatParticipantHistoryParseResult(null, error);
            if (result is null || result.Participants.Any(item => item.ProfileRequiresVerification))
                return new ChatParticipantHistoryParseResult(null,
                    "Не удалось достоверно подтвердить Instagram-профиль.");
            return new ChatParticipantHistoryParseResult(result.Participants.Select(item =>
                new ParticipantHistoryEntry(item.SubscriptionProfileUrl,
                    item.ActivityProfileUrl, item.Category,
                    IsWeekend: item.Status == ParticipantStatus.WEEKEND,
                    IsPenalty: item.Status == ParticipantStatus.PENALTY,
                    Position: item.Position, DisplayName: item.DisplayName)).ToArray(), null);
        }, cancellationToken);
    }
}
