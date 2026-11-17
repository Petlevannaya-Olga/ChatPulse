using System.Text.RegularExpressions;

namespace ChatPulse.Desktop.Services;

public enum ParticipantStatus
{
    ACTIVE,
    WEEKEND,
    PENALTY
}

public sealed record ParsedParticipant(int? Position, string SubscriptionProfileUrl,
    string ActivityProfileUrl, string Category, ParticipantStatus Status, string? DisplayName = null);

public sealed record ParsedParticipantList(IReadOnlyList<ParsedParticipant> Participants);

/// <summary>
/// Conservative parser for the numbered participant registers published in post chats.
/// Unrecognized messages are never treated as complete registers.
/// </summary>
public static class ParticipantListParser
{
    private static readonly TimeSpan REGEX_TIMEOUT = TimeSpan.FromMilliseconds(250);
    private static readonly Regex PROFILE_LINK = new(
        @"(?:https?://)?(?:www\.)?instagram\.com/[A-Za-z0-9._]+/?|https?://(?:www\.)?t\.me/(?:[A-Za-z0-9_]+|\+[A-Za-z0-9_-]+)/?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, REGEX_TIMEOUT);
    private static readonly Regex NUMBER = new(
        @"^\s*(\d{1,4})(?:\s+((?:SUPER\s+)?VIP\b.*))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant, REGEX_TIMEOUT);
    private static readonly Regex ANY_LINK = new(
        @"(?:https?://|www\.|(?:[a-z0-9-]+\.)+[a-z]{2,}/)[^\s<>\[\]()]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, REGEX_TIMEOUT);

    public static bool HasAnyLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { return ANY_LINK.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static bool TryParse(string? text, out ParsedParticipantList? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return false;
        try
        {
            var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            // Post chats use different register templates. Some start with administrative
            // roles instead of a heading such as "ВСЕ МЫ".
            var hasRegisterHeading = lines.Any(IsRegisterHeading);
            var administrativeHeadings = lines.Count(IsAdministratorHeading);
            // A Telegram participant register may have no explicit register heading.
            // Its numbered profile rows provide independent evidence of the format.
            if (!hasRegisterHeading && administrativeHeadings < 2 &&
                !HasNumberedProfileRows(lines)) return false;

            var entries = new List<ParsedParticipant>();
            var category = "Обычный";
            string? entryCategory = null;
            var status = ParticipantStatus.ACTIVE;
            int? position = null;
            string? subscription = null;
            string? activity = null;
            var nextIsActivity = false;
            string? administratorRole = null;
            string? administratorName = null;
            string? participantName = null;
            var numberedCount = 0;
            var weekendIsIndividual = false;

            void Complete()
            {
                if (string.IsNullOrEmpty(subscription) ||
                    (!position.HasValue && administratorRole is null)) return;
                entries.Add(new ParsedParticipant(position, subscription,
                    activity ?? subscription, administratorRole ?? entryCategory ?? category, status,
                    administratorName ?? participantName));
            }

            foreach (var original in lines)
            {
                var line = original.Trim();
                if (line.Length == 0) continue;
                if (line.Contains("VIP УЧАСТНИКИ", StringComparison.OrdinalIgnoreCase))
                {
                    Complete(); position = null; subscription = null; activity = null;
                    weekendIsIndividual = false;
                    category = "VIP"; status = ParticipantStatus.ACTIVE;
                    administratorRole = null; administratorName = null; participantName = null;
                    entryCategory = null; continue;
                }
                if (IsGeneralParticipantHeading(line))
                {
                    Complete(); position = null; subscription = null; activity = null;
                    weekendIsIndividual = false;
                    category = "Обычный"; status = ParticipantStatus.ACTIVE;
                    administratorRole = null; administratorName = null; participantName = null;
                    entryCategory = null; continue;
                }
                if (line.Contains("ШТРАФНЫЕ ДНИ", StringComparison.OrdinalIgnoreCase))
                {
                    Complete(); position = null; subscription = null; activity = null;
                    weekendIsIndividual = false;
                    status = ParticipantStatus.PENALTY;
                    administratorRole = null; administratorName = null; participantName = null;
                    entryCategory = null; continue;
                }
                if (line.Contains("ВЫХОДНЫЕ", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains(" с ", StringComparison.OrdinalIgnoreCase))
                {
                    Complete(); position = null; subscription = null; activity = null;
                    weekendIsIndividual = false;
                    status = ParticipantStatus.WEEKEND;
                    administratorRole = null; administratorName = null; participantName = null;
                    entryCategory = null; continue;
                }

                if (IsAdministratorHeading(line))
                {
                    Complete(); position = null; subscription = null; activity = null;
                    administratorRole = line; administratorName = null; participantName = null;
                    continue;
                }
                var number = NUMBER.Match(line);
                if (number.Success && !line.Contains("ЧАТ", StringComparison.OrdinalIgnoreCase))
                {
                    Complete();
                    if (weekendIsIndividual) { status = ParticipantStatus.ACTIVE; weekendIsIndividual = false; }
                    position = int.Parse(number.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    subscription = null; activity = null; nextIsActivity = false;
                    administratorRole = null; administratorName = null; participantName = null;
                    var categoryDetail = number.Groups[2].Value.Trim();
                    entryCategory = category == "VIP" && categoryDetail.Length > 0
                        ? categoryDetail : null;
                    numberedCount++;
                }
                if (line.Contains("Актив дает отсюда", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Актив даёт отсюда", StringComparison.OrdinalIgnoreCase))
                    nextIsActivity = true;

                foreach (Match match in PROFILE_LINK.Matches(line))
                {
                    if (!position.HasValue && administratorRole is null) continue;
                    var url = NormalizeProfile(match.Value, status == ParticipantStatus.PENALTY);
                    if (url is null) continue;
                    if (nextIsActivity && subscription is not null) activity = url;
                    else if (subscription is null) subscription = url;
                    else if (activity is null) activity = url;
                    nextIsActivity = false;
                }
                if (!position.HasValue && administratorRole is not null &&
                    subscription is null && !PROFILE_LINK.IsMatch(line) &&
                    !line.Contains("Подписка", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("ЛИНЕЙКИ", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("ВЕКТОРА", StringComparison.OrdinalIgnoreCase))
                    administratorName ??= line;
                // Some Telegram registers put the participant's display name after the URL.
                // Ignore headings, instructions and the URL itself.
                if (position.HasValue && subscription is not null &&
                    participantName is null && !PROFILE_LINK.IsMatch(line) &&
                    !line.Contains("Подписка", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("Актив", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("Выходные", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("ШТРАФНЫЕ", StringComparison.OrdinalIgnoreCase) &&
                    !IsAdministratorHeading(line) && !NUMBER.IsMatch(line))
                    participantName = line;
                if (position.HasValue && status == ParticipantStatus.ACTIVE &&
                    line.Contains("Выходные с", StringComparison.OrdinalIgnoreCase))
                {
                    status = ParticipantStatus.WEEKEND;
                    weekendIsIndividual = true;
                }
            }
            Complete();
            // A list of two links or a numbered task is not necessarily a participant register.
            if (numberedCount < 3 || entries.Count < 3 ||
                entries.Count(entry => entry.Position.HasValue) != numberedCount) return false;
            // Two separately numbered people may share the same Telegram link.
            // Preserve both rows; the registry must resolve identity conservatively.
            result = new ParsedParticipantList(entries);
            return true;
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static bool IsRegisterHeading(string line)
        => line.Contains("VIP УЧАСТНИКИ", StringComparison.OrdinalIgnoreCase) ||
           IsGeneralParticipantHeading(line);

    private static bool IsGeneralParticipantHeading(string line)
        => line.Contains("ВСЕ МЫ", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("СПИСОК УЧАСТНИКОВ", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("СПИСОК УЧАСТНИЦ", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("НАШИ УЧАСТНИКИ", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("НАШИ УЧАСТНИЦЫ", StringComparison.OrdinalIgnoreCase);

    private static bool IsAdministratorHeading(string line)
        => line.Contains("Основатель", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("Директор", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("Администратор", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("Модератор", StringComparison.OrdinalIgnoreCase);

    private static bool HasNumberedProfileRows(string[] lines)
    {
        var numbered = 0;
        var linked = 0;
        var awaitingProfile = false;
        foreach (var item in lines)
        {
            var line = item.Trim();
            if (NUMBER.IsMatch(line))
            {
                if (awaitingProfile) return false;
                numbered++;
                awaitingProfile = true;
            }
            else if (awaitingProfile && PROFILE_LINK.IsMatch(line))
            {
                linked++;
                awaitingProfile = false;
            }
        }
        return numbered >= 5 && linked >= 5 && !awaitingProfile;
    }

    private static string? NormalizeProfile(string raw, bool penalty)
    {
        var value = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? raw : "https://" + raw;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var username = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (string.IsNullOrWhiteSpace(username) || username.Contains('/')) return null;
        if (uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("www.t.me", StringComparison.OrdinalIgnoreCase))
        {
            // Invite links do not necessarily identify a person, but must be preserved
            // as supplied rather than silently rewritten as Instagram accounts.
            return $"https://t.me/{username}";
        }
        if (penalty && username.Length > 1 && (username.EndsWith('X') || username.EndsWith('Х')))
            username = username[..^1];
        if (username is "p" or "reel" or "stories" or "tv") return null;
        return $"https://www.instagram.com/{username}";
    }
}
