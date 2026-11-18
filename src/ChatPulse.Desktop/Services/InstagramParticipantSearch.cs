namespace ChatPulse.Desktop.Services;

public enum InstagramSearchStatus
{
    FOUND,
    NOT_FOUND,
    INCONCLUSIVE
}

public sealed record InstagramSearchResult(InstagramSearchStatus Status, string Details)
{
    public string Describe() => Status switch
    {
        InstagramSearchStatus.FOUND => "Найдена: точное совпадение username",
        InstagramSearchStatus.NOT_FOUND => "Не найдена в поиске (это не подтверждает отписку)",
        _ => $"Не удалось проверить: {Details}"
    };
}

public sealed record InstagramParticipantCheck(string UserName,
    InstagramSearchResult FollowsMe, InstagramSearchResult IFollow);

/// <summary>Search evidence must come from the list dialog, never from the whole page.</summary>
public static class InstagramSearchEvidence
{
    public static InstagramSearchStatus Classify(string requestedUserName,
        IEnumerable<string> resultLinks, bool confirmedEmptyResult,
        IEnumerable<string>? visibleResultUserNames = null)
    {
        var target = ChatPulse.Desktop.Settings.InstagramUserName.Normalize(requestedUserName);
        if (InstagramListCollectionParser.ParseProfileLinks(resultLinks).Contains(target))
            return InstagramSearchStatus.FOUND;
        if (visibleResultUserNames is not null && visibleResultUserNames.Any(name =>
                string.Equals(name.Trim().TrimStart('@'), target, StringComparison.OrdinalIgnoreCase)))
            return InstagramSearchStatus.FOUND;
        return confirmedEmptyResult ? InstagramSearchStatus.NOT_FOUND : InstagramSearchStatus.INCONCLUSIVE;
    }
}

/// <summary>Distinguishes a subscriber-list dialog from an unrelated Instagram modal.</summary>
public static class InstagramParticipantDialogTitle
{
    // The list title can be plain text even when unrelated HTML headings exist.
    public static bool MatchesDialog(string kind, IEnumerable<string> headings,
        IEnumerable<string> dialogLines)
        => Matches(kind, headings) || Matches(kind, dialogLines);

    public static bool Matches(string kind, IEnumerable<string> titleLines)
    {
        string[] validTitles = kind switch
        {
            "followers" => new[] { "followers", "подписчики" },
            "following" => new[] { "following", "подписки", "ваши подписки", "your following" },
            _ => []
        };
        return titleLines.Take(3).Any(line =>
            validTitles.Any(title => string.Equals(line.Trim(), title, StringComparison.OrdinalIgnoreCase)));
    }
}
