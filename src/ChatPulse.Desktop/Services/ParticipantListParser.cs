using System.Text.RegularExpressions;

namespace ChatPulse.Desktop.Services;

public enum ParticipantStatus
{
    ACTIVE,
    WEEKEND,
    PENALTY
}

public sealed record ParsedParticipant(int? Position, string SubscriptionProfileUrl,
    string ActivityProfileUrl, string Category, ParticipantStatus Status, string? DisplayName = null,
    bool ProfileRequiresVerification = false);

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
    // A penalty URL may contain a Cyrillic X suffix. Latin X is a valid Instagram
    // character, so it must not be removed without further evidence.
    private static readonly Regex PENALTY_PROFILE_LINK = new(
        @"(?:https?://)?(?:www\.)?instagram\.com/[A-Za-z0-9._]+[Хх]?/?|https?://(?:www\.)?t\.me/(?:[A-Za-z0-9_]+|\+[A-Za-z0-9_-]+)/?",
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

    /// <summary>
    /// Identifies candidate participant registers without requiring them to parse successfully.
    /// A malformed newest register must not be silently skipped in favor of an older one.
    /// </summary>
    public static bool LooksLikeParticipantList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return false;
        try
        {
            var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            var numbered = lines.Count(line => NUMBER.IsMatch(line.Trim()));
            if (numbered == 0) return false;
            return lines.Any(IsRegisterHeading) ||
                   lines.Count(IsAdministratorHeading) >= 2 ||
                   lines.Any(line => line.Contains("ШТРАФНЫЕ ДНИ", StringComparison.OrdinalIgnoreCase)) ||
                   numbered >= 5 && lines.Count(HasAnyLink) >= numbered / 2;
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static bool TryParse(string? text, out ParsedParticipantList? result,
        Func<string, bool?>? profileOpens = null, bool instagramOnly = false)
        => TryParse(text, out result, out _, profileOpens, instagramOnly);

    /// <summary>
    /// Returns the first verified parsing error for display in the synchronization
    /// report. Successful parsing follows exactly the same rules as TryParse above.
    /// </summary>
    public static bool TryParse(string? text, out ParsedParticipantList? result,
        out string? failureReason, Func<string, bool?>? profileOpens = null,
        bool instagramOnly = false)
    {
        result = null;
        failureReason = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            failureReason = "Сообщение пустое.";
            return false;
        }
        if (text.Length > 100_000)
        {
            failureReason = "Текст списка превышает допустимый размер.";
            return false;
        }
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
                !HasNumberedProfileRows(lines))
            {
                failureReason = "Не удалось распознать структуру списка участниц: отсутствуют " +
                                "заголовки разделов и достаточное число записей с профилями.";
                return false;
            }

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
            string? firstIncompleteEntry = null;

            void Complete()
            {
                if (string.IsNullOrEmpty(subscription))
                {
                    // Missing profile was already a parsing failure for numbered
                    // rows. Preserve its position rather than reporting only a count mismatch.
                    if (position.HasValue)
                        firstIncompleteEntry ??= $"{DescribeEntry(position, administratorRole, category, status)}: " +
                                                "отсутствует корректная ссылка на Instagram-профиль.";
                    return;
                }
                if (!position.HasValue && administratorRole is null) return;
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

                // Every link in an Instagram register must have an Instagram domain.
                // Only the penalty section tolerates the Cyrillic X suffix.
                if (instagramOnly && (position.HasValue || administratorRole is not null))
                {
                    foreach (Match rawLink in ANY_LINK.Matches(line))
                    {
                        var normalized = NormalizeProfile(rawLink.Value,
                            status == ParticipantStatus.PENALTY);
                        if (normalized is null ||
                            !normalized.StartsWith("https://www.instagram.com/", StringComparison.OrdinalIgnoreCase))
                        {
                            failureReason = DescribeInvalidLink(rawLink.Value,
                                position, administratorRole, category, status);
                            return false;
                        }
                    }
                }
                var links = status == ParticipantStatus.PENALTY ? PENALTY_PROFILE_LINK : PROFILE_LINK;
                foreach (Match match in links.Matches(line))
                {
                    if (!position.HasValue && administratorRole is null) continue;
                    var url = NormalizeProfile(match.Value, status == ParticipantStatus.PENALTY);
                    if (url is null) continue;
                    if (instagramOnly &&
                        !url.StartsWith("https://www.instagram.com/", StringComparison.OrdinalIgnoreCase))
                    {
                        failureReason = DescribeInvalidLink(match.Value,
                            position, administratorRole, category, status);
                        return false;
                    }
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
            if (firstIncompleteEntry is not null)
            {
                failureReason = firstIncompleteEntry;
                return false;
            }
            if (numberedCount < 3 || entries.Count < 3 ||
                entries.Count(entry => entry.Position.HasValue) != numberedCount)
            {
                failureReason = "Не удалось выделить полный список: " +
                                $"пронумерованных записей — {numberedCount}, распознано — " +
                                $"{entries.Count(entry => entry.Position.HasValue)}. " +
                                "Проверьте наличие Instagram-ссылки у каждой записи.";
                return false;
            }
            // A repeated Instagram profile with the same activity profile is one
            // participant, even when it appears under two different list numbers.
            // Telegram profiles can be shared by different people, so do not collapse
            // those records (or conflicting Instagram activity profiles).
            var prepared = instagramOnly ? MergeDuplicateInstagramEntries(entries) : entries;
            if (!TryMergePenaltyEntries(prepared, out var participants,
                    out failureReason, profileOpens)) return false;
            result = new ParsedParticipantList(participants);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            failureReason = "Превышено время анализа текста списка участниц.";
            return false;
        }
    }

    private static string DescribeEntry(int? position, string? administratorRole,
        string category, ParticipantStatus status)
    {
        if (administratorRole is not null)
            return $"Раздел «Администрация», {administratorRole.Trim()}";
        var section = status switch
        {
            ParticipantStatus.PENALTY => "ШТРАФНЫЕ ДНИ",
            ParticipantStatus.WEEKEND => "ВЫХОДНЫЕ",
            _ when category == "VIP" => "VIP УЧАСТНИКИ",
            _ => "ВСЕ МЫ"
        };
        return position.HasValue ? $"Раздел «{section}», запись №{position.Value}"
            : $"Раздел «{section}»";
    }

    private static string DescribeInvalidLink(string raw, int? position,
        string? administratorRole, string category, ParticipantStatus status)
    {
        var location = DescribeEntry(position, administratorRole, category, status);
        var value = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? raw : "https://" + raw;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return $"{location}: некорректная ссылка «{raw}».";
        if (!uri.Host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.instagram.com", StringComparison.OrdinalIgnoreCase))
            return $"{location}: недопустимый домен «{uri.Host}» в ссылке «{raw}». " +
                   "Ожидается instagram.com.";
        return $"{location}: некорректное имя Instagram в ссылке «{raw}». " +
               "Завершающая кириллическая Х допускается только в штрафном разделе.";
    }

    private static IReadOnlyList<ParsedParticipant> MergeDuplicateInstagramEntries(
        IEnumerable<ParsedParticipant> entries)
    {
        var distinct = new List<ParsedParticipant>();
        foreach (var item in entries)
        {
            if (item.Status == ParticipantStatus.PENALTY)
            {
                distinct.Add(item);
                continue;
            }
            if (distinct.Any(existing => existing.Status != ParticipantStatus.PENALTY &&
                existing.SubscriptionProfileUrl.Equals(item.SubscriptionProfileUrl,
                    StringComparison.OrdinalIgnoreCase) &&
                existing.ActivityProfileUrl.Equals(item.ActivityProfileUrl,
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            distinct.Add(item);
        }
        return distinct;
    }

    private static bool TryMergePenaltyEntries(IReadOnlyList<ParsedParticipant> entries,
        out IReadOnlyList<ParsedParticipant> participants, out string? failureReason,
        Func<string, bool?>? profileOpens)
    {
        failureReason = null;
        var merged = entries.Where(item => item.Status != ParticipantStatus.PENALTY).ToList();
        foreach (var penalty in entries.Where(item => item.Status == ParticipantStatus.PENALTY))
        {
            var exactMatches = FindMatches(merged, penalty.SubscriptionProfileUrl);
            var shortened = TryWithoutLatinXSuffix(penalty.SubscriptionProfileUrl);
            var shortenedMatches = shortened is null ? Array.Empty<int>() : FindMatches(merged, shortened);
            var matches = exactMatches.Concat(shortenedMatches).Distinct().ToArray();
            if (matches.Length > 1)
            {
                // If both names belong to different participants, neither the
                // suffix convention nor the original URL identifies the right one.
                participants = [];
                failureReason = $"{DescribeEntry(penalty.Position, null, penalty.Category, penalty.Status)}: " +
                                $"профиль «{penalty.SubscriptionProfileUrl}» неоднозначно совпадает " +
                                "с несколькими участницами (с учётом возможного суффикса X).";
                return false;
            }
            if (matches.Length == 1)
            {
                var index = matches[0];
                merged[index] = merged[index] with { Status = ParticipantStatus.PENALTY };
                continue;
            }

            var resolved = penalty.SubscriptionProfileUrl;
            // Only an unmatched Latin X in the penalty section needs a browser
            // check. Ordinary entries and penalty URLs without X are never probed.
            if (shortened is not null && profileOpens is not null)
            {
                // A timeout, login redirect or restriction is inconclusive,
                // not proof that either profile does not exist.
                var original = profileOpens(resolved);
                var alternative = profileOpens(shortened);
                if (original == false && alternative == true) resolved = shortened;
                else if (original != true || alternative != false)
                {
                    participants = [];
                    failureReason = $"{DescribeEntry(penalty.Position, null, penalty.Category, penalty.Status)}: " +
                                    $"не удалось однозначно подтвердить Instagram-профили " +
                                    $"«{resolved}» и «{shortened}».";
                    return false;
                }
            }
            // Without a browser result, keep a Latin X literally: removing a
            // legitimate final character would silently check someone else.
            // A Cyrillic X has already been removed during normalization because
            // it is not a legal character in an Instagram username.
            merged.Add(penalty with
            {
                SubscriptionProfileUrl = resolved,
                ActivityProfileUrl = resolved,
                ProfileRequiresVerification = shortened is not null && profileOpens is null
            });
        }
        participants = merged;
        return true;
    }

    private static int[] FindMatches(IReadOnlyList<ParsedParticipant> participants, string url)
        => participants.Select((item, index) => (item, index))
            .Where(candidate =>
                candidate.item.SubscriptionProfileUrl.Equals(url, StringComparison.OrdinalIgnoreCase) ||
                candidate.item.ActivityProfileUrl.Equals(url, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.index).ToArray();

    private static string? TryWithoutLatinXSuffix(string url)
    {
        if (!url.StartsWith("https://www.instagram.com/", StringComparison.OrdinalIgnoreCase) ||
            url.Length < "https://www.instagram.com/".Length + 2 || url[^1] is not ('x' or 'X'))
            return null;
        return url[..^1];
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
        // Never silently rewrite arbitrary domains (for example www.com) into
        // Instagram URLs. Their author must correct the source message.
        if (!uri.Host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.instagram.com", StringComparison.OrdinalIgnoreCase))
            return null;
        if (penalty && username[^1] is ('Х' or 'х'))
        {
            if (username.Length < 2) return null;
            username = username[..^1];
        }
        // Any other non-ASCII ending is not an Instagram username.
        if (username.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_')))
            return null;
        if (username is "p" or "reel" or "stories" or "tv") return null;
        return $"https://www.instagram.com/{username}";
    }
}
