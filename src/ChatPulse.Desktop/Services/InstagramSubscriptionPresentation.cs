using System.Globalization;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// A participant in the current chat register. Display filtering is deliberately independent
/// of the eligibility rules used by the bulk Instagram check.
/// </summary>
public sealed record InstagramSubscriptionRow(
    long ParticipantId, string DisplayName, string Category,
    string ActivityUserName, string ActivityProfileUrl, string SubscriptionUserName,
    string FollowsMeStatus, string IFollowStatus, string LastCheck)
{
    public bool HasActivityProfile => !string.IsNullOrWhiteSpace(ActivityUserName);
    /// <summary>Главный Instagram-профиль, на который нужно подписаться.</summary>
    public string ProfileUserNameLabel => !string.IsNullOrWhiteSpace(SubscriptionUserName)
        ? $"@{SubscriptionUserName}"
        : HasActivityProfile ? $"@{ActivityUserName}" : "—";
    public string ActivityUserNameLabel => $"@{ActivityUserName}";
    public bool HasDifferentActivityProfile => HasActivityProfile &&
        !string.IsNullOrWhiteSpace(SubscriptionUserName) &&
        !string.Equals(SubscriptionUserName, ActivityUserName, StringComparison.OrdinalIgnoreCase);
    public string MainProfileLabel => !string.IsNullOrWhiteSpace(SubscriptionUserName) && HasDifferentActivityProfile
        ? $"Для подписки: @{SubscriptionUserName}"
        : HasActivityProfile ? $"Актив: @{ActivityUserName}" :
            !string.IsNullOrWhiteSpace(SubscriptionUserName) ? $"Для подписки: @{SubscriptionUserName}" :
                "Instagram-профиль не указан";
    public string ActivityProfileLabel => HasDifferentActivityProfile
        ? $"Даёт актив с другой страницы: @{ActivityUserName}" : string.Empty;
    public string FollowsMeLabel => Describe(FollowsMeStatus);
    public string IFollowLabel => Describe(IFollowStatus);
    public bool HasDiscrepancy => FollowsMeStatus == "NOT_FOUND" || IFollowStatus == "NOT_FOUND";
    public bool IsUnverified => (FollowsMeStatus != "NOT_APPLICABLE" &&
                                FollowsMeStatus is not ("FOUND" or "NOT_FOUND")) ||
                                IFollowStatus is not ("FOUND" or "NOT_FOUND");
    public bool CanOpenProfile => ProfileActionUri is not null;
    public Uri? ProfileActionUri => TryGetInstagramUri(ProfileActionUrl);
    public Uri? ActivityProfileUri => HasDifferentActivityProfile
        ? TryGetInstagramUri(ActivityProfileUrl) : null;
    public bool CanCheckFollowing => !string.IsNullOrWhiteSpace(SubscriptionUserName);
    public bool CanCheckFollowers => HasActivityProfile && FollowsMeStatus != "NOT_APPLICABLE";
    // A missing "I follow her" result must lead to the account to follow, not the activity account.
    public string ProfileActionUrl => !string.IsNullOrWhiteSpace(SubscriptionUserName)
        ? $"https://www.instagram.com/{SubscriptionUserName}/"
        : HasActivityProfile ? ActivityProfileUrl : string.Empty;
    public string ProfileActionLabel => IFollowStatus == "NOT_FOUND" ? "Открыть профиль" : "Instagram";

    private static Uri? TryGetInstagramUri(string profileUrl) =>
        InstagramActivityProfile.TryGetUserName(profileUrl) is not null &&
        Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri) ? uri : null;

    private static string Describe(string status) => status switch
    {
        "FOUND" => "Да",
        "NOT_FOUND" => "Нет",
        "INCONCLUSIVE" => "Ошибка проверки",
        "NOT_APPLICABLE" => "—",
        _ => "Не проверено"
    };
}

public static class InstagramSubscriptionPresentation
{
    public static IReadOnlyList<InstagramSubscriptionRow> Build(
        IEnumerable<InstagramParticipantOption> participants,
        IEnumerable<InstagramSavedCheck> checks,
        IReadOnlySet<string> ownUserNames,
        IReadOnlySet<string>? exemptFromFollowersUserNames = null)
    {
        var indexed = checks.ToDictionary(
            entry => (entry.UserName.ToLowerInvariant(), entry.Kind.ToLowerInvariant()),
            entry => entry);
        var results = new List<InstagramSubscriptionRow>();
        foreach (var participant in participants)
        {
            if (InstagramOwnAccountFilter.IsOwnParticipant(participant, ownUserNames)) continue;
            var userName = participant.UserName;
            var subscriptionUserName = InstagramActivityProfile.TryGetUserName(
                participant.SubscriptionProfileUrl) ?? string.Empty;
            var exemptFromFollowers = InstagramBatchEligibility.IsExcluded(participant.Category) ||
                                      (exemptFromFollowersUserNames?.Contains(userName) ?? false);
            InstagramSavedCheck? followers = null;
            if (!exemptFromFollowers)
                indexed.TryGetValue((userName.ToLowerInvariant(), "followers"), out followers);
            InstagramSavedCheck? following = null;
            if (!string.IsNullOrWhiteSpace(subscriptionUserName))
                indexed.TryGetValue((subscriptionUserName.ToLowerInvariant(), "following"), out following);
            var checkedAt = new[] { followers?.CheckedAtUtc, following?.CheckedAtUtc }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .OrderByDescending(value => value, StringComparer.Ordinal)
                .FirstOrDefault();
            var lastCheck = DateTimeOffset.TryParse(checkedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)
                ? parsed.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))
                : "—";
            results.Add(new InstagramSubscriptionRow(
                participant.ParticipantId,
                string.IsNullOrWhiteSpace(participant.DisplayName)
                    ? (string.IsNullOrWhiteSpace(userName) ? "Участница" : $"@{userName}")
                    : participant.DisplayName,
                participant.Category, userName, participant.ActivityProfileUrl,
                subscriptionUserName, exemptFromFollowers ? "NOT_APPLICABLE" :
                    followers?.Status ?? "UNVERIFIED",
                following?.Status ?? "UNVERIFIED", lastCheck));
        }
        // Keep the order from the Telegram list within each category.
        // OrderBy is stable: administration first, then VIP, then regular participants.
        return results.OrderBy(row => InstagramParticipantCategoryCounts.GetDisplayPriority(row.Category))
            .ToArray();
    }
}


/// <summary>Mutually exclusive counts for the current chat, excluding own accounts.</summary>
public sealed record InstagramParticipantCategoryCounts(int Administration, int Vip, int Regular)
{
    public int Total => Administration + Vip + Regular;

    // Use the same category classification for counts and the display order.
    internal static int GetDisplayPriority(string? category) =>
        IsAdministration(category) ? 0 : IsVip(category) ? 1 : 2;

    /// <summary>Headcount in the Telegram register, including the owner's accounts.</summary>
    public static InstagramParticipantCategoryCounts FromRegister(
        IEnumerable<InstagramParticipantOption> participants) =>
        FromCategories(participants.Select(participant => participant.Category));

    /// <summary>Headcount eligible for display/checks after own accounts are excluded.</summary>
    public static InstagramParticipantCategoryCounts FromParticipants(
        IEnumerable<InstagramParticipantOption> participants, IReadOnlySet<string> ownUserNames) =>
        FromCategories(participants
            .Where(participant => !InstagramOwnAccountFilter.IsOwnParticipant(participant, ownUserNames))
            .Select(participant => participant.Category));

    public static InstagramParticipantCategoryCounts FromRows(IEnumerable<InstagramSubscriptionRow> rows) =>
        FromCategories(rows.Select(row => row.Category));

    private static InstagramParticipantCategoryCounts FromCategories(IEnumerable<string?> categories)
    {
        var administration = 0;
        var vip = 0;
        var regular = 0;
        foreach (var category in categories)
        {
            if (IsAdministration(category)) administration++;
            else if (IsVip(category)) vip++;
            else regular++;
        }
        return new InstagramParticipantCategoryCounts(administration, vip, regular);
    }

    private static bool IsAdministration(string? category) =>
        Contains(category, "АДМИН") || Contains(category, "МОДЕРАТОР") ||
        Contains(category, "ОСНОВАТЕЛ") || Contains(category, "ДИРЕКТОР");

    private static bool IsVip(string? category) =>
        Contains(category, "VIP") || Contains(category, "ВИП");

    private static bool Contains(string? value, string fragment) =>
        value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;
}
