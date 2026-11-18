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
    public bool CanOpenProfile => HasActivityProfile;
    public string ProfileActionLabel => IFollowStatus == "NOT_FOUND" ? "Открыть профиль" : "Instagram";

    private static string Describe(string status) => status switch
    {
        "FOUND" => "Да",
        "NOT_FOUND" => "Нет в поиске",
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
            indexed.TryGetValue((userName.ToLowerInvariant(), "following"), out var following);
            var checkedAt = new[] { followers?.CheckedAtUtc, following?.CheckedAtUtc }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .OrderByDescending(value => value, StringComparer.Ordinal)
                .FirstOrDefault();
            var lastCheck = DateTimeOffset.TryParse(checkedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)
                ? parsed.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.GetCultureInfo("ru-RU"))
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
        return results;
    }
}
