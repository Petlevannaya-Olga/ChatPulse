using System.Globalization;
using System.Text.RegularExpressions;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Parsed full-list snapshot, or an explicit reason it was rejected. Rejected
/// snapshots must not be used to infer that anybody has left the chat.
/// </summary>
public sealed record ChatParticipantHistoryParseResult(
    IReadOnlyList<ParticipantHistoryEntry>? Entries, string? Error)
{
    public bool IsSuccess => Entries is { Count: > 0 } && Error is null;
}

public interface IChatParticipantHistoryParser
{
    ParticipantHistoryRule Rule { get; }

    bool IsCandidate(string? text);

    Task<ChatParticipantHistoryParseResult> ParseAsync(string? text, DateTime? sentAtUtc,
        IReadOnlySet<string> previouslySeenUsernames,
        Func<string, CancellationToken, Task<bool?>>? profileOpens,
        CancellationToken cancellationToken);
}

/// <summary>
/// Rules for the specific daily register of chat 14. The rest of ChatPulse does
/// not have to follow the same structure or accept malformed penalty URLs.
/// </summary>
public sealed class Chat14ParticipantHistoryParser : IChatParticipantHistoryParser
{
    private static readonly TimeSpan REGEX_TIMEOUT = TimeSpan.FromMilliseconds(250);
    private static readonly Regex NUMBER_LINE = new(
        @"^\s*(?<number>\d{1,4})(?:\s+(?<detail>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, REGEX_TIMEOUT);
    private static readonly Regex URL = new(
        @"(?:https?://)?(?:www\.)?[A-Za-z0-9.-]+\.[A-Za-z]{2,}/[^\s<>\[\]()]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, REGEX_TIMEOUT);
    private static readonly Regex WEEKEND_DATES = new(
        @"Выходные\s+с\s+(?<from>\d{1,2})\s+по\s+(?<to>\d{1,2})\s+(?<month>[а-яё]+)(?:\s+(?<year>\d{4}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, REGEX_TIMEOUT);

    private enum Section { NONE, ADMIN, VIP, REGULAR, WEEKEND, PENALTY }

    private sealed class RawEntry(Section section, int? position, string category)
    {
        public Section Section { get; } = section;
        public int? Position { get; } = position;
        public string Category { get; } = category;
        public string? DisplayName { get; set; }
        public string? SubscriptionLink { get; set; }
        public string? ActivityLink { get; set; }
        public bool NextLinkIsActivity { get; set; }
        public bool IsWeekend { get; set; } = section == Section.WEEKEND;
        public string? WeekendSourceText { get; set; }
        public DateOnly? WeekendFrom { get; set; }
        public DateOnly? WeekendTo { get; set; }
    }

    public ParticipantHistoryRule Rule { get; } = new(1, "Chat14Rules", "{}");

    public bool IsCandidate(string? text)
    {
        if (ParticipantListParser.LooksLikeParticipantList(text)) return true;
        // An intermediate full list may have broken numbering or links. Do not
        // silently skip it when its two section headings identify a register.
        return !string.IsNullOrWhiteSpace(text) && text.Length <= 100_000 &&
               text.Contains("ВСЕ МЫ", StringComparison.OrdinalIgnoreCase) &&
               (text.Contains("VIP УЧАСТНИКИ", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("ШТРАФНЫЕ ДНИ", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ChatParticipantHistoryParseResult> ParseAsync(string? text, DateTime? sentAtUtc,
        IReadOnlySet<string> previouslySeenUsernames,
        Func<string, CancellationToken, Task<bool?>>? profileOpens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previouslySeenUsernames);
        if (string.IsNullOrWhiteSpace(text)) return Failed("Сообщение пустое.");
        if (text.Length > 100_000) return Failed("Размер списка превышает допустимый предел.");

        var raw = new List<RawEntry>();
        var section = Section.NONE;
        var vipHeading = false;
        var regularHeading = false;
        // Some chat-14 registers print «ВСЕ МЫ» before the final VIP's URL.
        // Keep that incomplete VIP entry open until its profile is read, then
        // move to the regular section. A missing VIP URL must still be rejected.
        var regularHeadingBeforeVipLink = false;
        RawEntry? current = null;
        string? error = null;

        bool FinishEntry()
        {
            if (current is null) return true;
            if (current.SubscriptionLink is null)
            {
                error = $"{Location(current)}: отсутствует ссылка на Instagram-профиль.";
                return false;
            }
            raw.Add(current);
            current = null;
            return true;
        }

        try
        {
            foreach (var original in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = original.Trim().Replace("\\_", "_", StringComparison.Ordinal);
                if (line.Length == 0) continue;
                if (line.Contains("VIP УЧАСТНИКИ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!FinishEntry()) return Failed(error!);
                    section = Section.VIP;
                    vipHeading = true;
                    continue;
                }
                if (line.Contains("ВСЕ МЫ", StringComparison.OrdinalIgnoreCase))
                {
                    regularHeading = true;
                    if (section == Section.VIP && current is { SubscriptionLink: null })
                    {
                        // The header is visually placed inside the last VIP row;
                        // the next profile URL still belongs to that VIP.
                        regularHeadingBeforeVipLink = true;
                        continue;
                    }
                    if (!FinishEntry()) return Failed(error!);
                    section = Section.REGULAR;
                    continue;
                }
                if (line.Contains("ШТРАФНЫЕ ДНИ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!FinishEntry()) return Failed(error!);
                    section = Section.PENALTY;
                    continue;
                }
                if (line.Contains("ВЫХОДНЫЕ", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains(" с ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!FinishEntry()) return Failed(error!);
                    section = Section.WEEKEND;
                    continue;
                }
                if (IsAdminHeading(line))
                {
                    if (!FinishEntry()) return Failed(error!);
                    section = Section.ADMIN;
                    current = new RawEntry(section, null, TrimDecorations(line));
                    continue;
                }

                var number = NUMBER_LINE.Match(line);
                if (number.Success && !line.Contains("ЧАТ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!FinishEntry()) return Failed(error!);
                    if (section is Section.NONE or Section.ADMIN)
                        return Failed("Пронумерованная запись находится вне раздела списка участниц.");
                    var detail = TrimDecorations(number.Groups["detail"].Value);
                    var category = section == Section.VIP
                        ? (detail.Length > 0 ? detail : "VIP") : "Обычный";
                    current = new RawEntry(section,
                        int.Parse(number.Groups["number"].Value, CultureInfo.InvariantCulture), category);
                    continue;
                }
                if (current is null) continue;
                if (line.Contains("Актив дает отсюда", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Актив даёт отсюда", StringComparison.OrdinalIgnoreCase))
                    current.NextLinkIsActivity = true;
                if (line.Contains("Выходные с", StringComparison.OrdinalIgnoreCase))
                {
                    current.IsWeekend = true;
                    current.WeekendSourceText = line;
                    if (TryReadWeekendDates(line, sentAtUtc, out var from, out var to))
                    {
                        current.WeekendFrom = from;
                        current.WeekendTo = to;
                    }
                }

                var urls = URL.Matches(line).Cast<Match>()
                    .Select(match => match.Value.TrimEnd('\\', ',', ';', '.', '!', '?', '*', ')'))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var url in urls)
                {
                    if (current.NextLinkIsActivity && current.SubscriptionLink is not null)
                    {
                        if (current.ActivityLink is not null &&
                            !current.ActivityLink.Equals(url, StringComparison.OrdinalIgnoreCase))
                            return Failed($"{Location(current)}: несколько разных профилей для актива.");
                        current.ActivityLink = url;
                    }
                    else if (current.SubscriptionLink is null) current.SubscriptionLink = url;
                    else if (current.ActivityLink is null &&
                             !current.SubscriptionLink.Equals(url, StringComparison.OrdinalIgnoreCase))
                        current.ActivityLink = url;
                    else if (current.ActivityLink is not null &&
                             !current.ActivityLink.Equals(url, StringComparison.OrdinalIgnoreCase) &&
                             !current.SubscriptionLink.Equals(url, StringComparison.OrdinalIgnoreCase))
                        return Failed($"{Location(current)}: обнаружена дополнительная неизвестная ссылка.");
                    current.NextLinkIsActivity = false;
                }
                if (regularHeadingBeforeVipLink && current.SubscriptionLink is not null)
                {
                    // Finish the pending VIP only after seeing its Instagram URL;
                    // the following numbered records are ordinary participants.
                    if (!FinishEntry()) return Failed(error!);
                    regularHeadingBeforeVipLink = false;
                    section = Section.REGULAR;
                    continue;
                }
                if (section == Section.ADMIN && current.DisplayName is null && urls.Length == 0 &&
                    !line.Contains("Подписка", StringComparison.OrdinalIgnoreCase))
                    current.DisplayName = TrimDecorations(line);
            }
            if (!FinishEntry()) return Failed(error!);
            if (!vipHeading || !regularHeading || raw.Count < 3 ||
                raw.Count(item => item.Position.HasValue) < 3)
                return Failed("Не подтверждена полнота списка 14-го чата: ожидаются разделы VIP, «ВСЕ МЫ» и записи участниц.");

            // Resolve suffixes against the principal section first, then previously
            // seen usernames. An activity account must never be used as identity.
            var principalUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in raw.Where(item => item.Section != Section.PENALTY))
            {
                if (!TryReadUsername(item.SubscriptionLink!, allowDamagedDomain: false,
                        out var username, out _, out var explanation))
                    return Failed($"{Location(item)}: {explanation}");
                principalUsernames.Add(username);
            }
            var known = new HashSet<string>(previouslySeenUsernames, StringComparer.OrdinalIgnoreCase);
            known.UnionWith(principalUsernames);
            var parsed = new List<ParticipantHistoryEntry>();
            foreach (var item in raw)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadUsername(item.SubscriptionLink!, item.Section == Section.PENALTY,
                        out var username, out var hasLatinX, out var explanation))
                    return Failed($"{Location(item)}: {explanation}");
                if (item.Section == Section.PENALTY && hasLatinX)
                {
                    var shortened = username[..^1];
                    var fullKnown = known.Contains(username);
                    var shortKnown = known.Contains(shortened);
                    if (fullKnown && !shortKnown) { /* exact match */ }
                    else if (!fullKnown && shortKnown) username = shortened;
                    else
                    {
                        if (profileOpens is null)
                            return Failed($"{Location(item)}: ссылка с латинской X «{item.SubscriptionLink}» " +
                                "неоднозначна; требуется проверка обоих Instagram-профилей.");
                        var first = await profileOpens(ProfileUrl(username), cancellationToken);
                        var second = await profileOpens(ProfileUrl(shortened), cancellationToken);
                        if (first == true && second == false) { /* keep X */ }
                        else if (first == false && second == true) username = shortened;
                        else return Failed($"{Location(item)}: нельзя достоверно выбрать между " +
                            $"@{username} и @{shortened} (проверка Instagram неоднозначна).");
                    }
                }
                var activity = item.ActivityLink is null ? ProfileUrl(username) : string.Empty;
                if (item.ActivityLink is not null)
                {
                    if (!TryReadUsername(item.ActivityLink, allowDamagedDomain: false,
                            out var activityUsername, out _, out explanation))
                        return Failed($"{Location(item)}: профиль для актива: {explanation}");
                    activity = ProfileUrl(activityUsername);
                }
                parsed.Add(new ParticipantHistoryEntry(ProfileUrl(username), activity,
                    item.Category, item.IsWeekend, item.Section == Section.PENALTY,
                    item.Position, item.DisplayName, SectionName(item.Section), item.SubscriptionLink,
                    item.WeekendFrom, item.WeekendTo, item.WeekendSourceText));
            }
            // Keep the set of previously seen usernames free from snapshots the
            // history store would reject for conflicting duplicate primary rows.
            foreach (var group in parsed.GroupBy(item => item.SubscriptionProfileUrl,
                         StringComparer.OrdinalIgnoreCase))
            {
                var primary = group.Where(item =>
                    item.SourceSection is not ("ШТРАФНЫЕ ДНИ" or "ВЫХОДНЫЕ")).ToArray();
                if (primary.Length > 1 && primary.Skip(1).Any(item =>
                        !item.ActivityProfileUrl.Equals(primary[0].ActivityProfileUrl,
                            StringComparison.OrdinalIgnoreCase) ||
                        !item.Category.Equals(primary[0].Category, StringComparison.OrdinalIgnoreCase)))
                    return Failed($"Противоречащие повторные записи для {group.Key}.");
                var periods = group.Where(item => item.WeekendFromDate.HasValue ||
                        item.WeekendToDate.HasValue)
                    .Select(item => (item.WeekendFromDate, item.WeekendToDate))
                    .Distinct().ToArray();
                if (periods.Length > 1)
                    return Failed($"Противоречащие периоды выходных для {group.Key}.");
            }
            return new ChatParticipantHistoryParseResult(parsed, null);
        }
        catch (RegexMatchTimeoutException)
        {
            return Failed("Превышено время анализа текста списка участниц.");
        }
    }

    private static ChatParticipantHistoryParseResult Failed(string explanation) => new(null, explanation);

    private static bool TryReadUsername(string link, bool allowDamagedDomain,
        out string username, out bool hasLatinX, out string explanation)
    {
        username = string.Empty;
        hasLatinX = false;
        explanation = string.Empty;
        var input = link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? link : "https://" + link;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            explanation = $"некорректный URL «{link}».";
            return false;
        }
        if (!allowDamagedDomain && uri.Host is not ("instagram.com" or "www.instagram.com"))
        {
            explanation = $"недопустимый домен «{uri.Host}» в ссылке «{link}»; требуется instagram.com.";
            return false;
        }
        var candidate = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (candidate.Length == 0 || candidate.Contains('/'))
        {
            explanation = $"невозможно извлечь единственный Instagram username из «{link}».";
            return false;
        }
        if (allowDamagedDomain && candidate[^1] is ('Х' or 'х')) candidate = candidate[..^1];
        try { username = InstagramUserName.Normalize(candidate); }
        catch (ArgumentException)
        {
            explanation = $"некорректное Instagram-имя в ссылке «{link}».";
            return false;
        }
        hasLatinX = allowDamagedDomain && username.Length > 1 && username[^1] == 'x';
        return true;
    }

    private static string SectionName(Section section) => section switch
    {
        Section.ADMIN => "Администрация",
        Section.VIP => "VIP УЧАСТНИКИ",
        Section.WEEKEND => "ВЫХОДНЫЕ",
        Section.PENALTY => "ШТРАФНЫЕ ДНИ",
        _ => "ВСЕ МЫ"
    };

    private static string Location(RawEntry item) => item.Position.HasValue
        ? $"Раздел «{SectionName(item.Section)}», запись №{item.Position.Value}"
        : $"Раздел «{SectionName(item.Section)}»";

    private static bool IsAdminHeading(string line)
        => line.Contains("Основатель", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("Директор", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("Администратор", StringComparison.OrdinalIgnoreCase) ||
           line.Contains("Модератор", StringComparison.OrdinalIgnoreCase);

    private static string TrimDecorations(string value)
        => value.Trim().Trim('*', ' ', '⚡', '\uFE0F').Trim();

    private static string ProfileUrl(string username) => $"https://www.instagram.com/{username}";

    private static bool TryReadWeekendDates(string line, DateTime? date,
        out DateOnly? fromDate, out DateOnly? toDate)
    {
        fromDate = null;
        toDate = null;
        var match = WEEKEND_DATES.Match(line);
        if (!match.Success || !date.HasValue) return false;
        var name = match.Groups["month"].Value.ToLowerInvariant();
        var month = name switch
        {
            _ when name.StartsWith("январ", StringComparison.Ordinal) => 1,
            _ when name.StartsWith("феврал", StringComparison.Ordinal) => 2,
            _ when name.StartsWith("март", StringComparison.Ordinal) => 3,
            _ when name.StartsWith("апрел", StringComparison.Ordinal) => 4,
            _ when name.StartsWith("ма", StringComparison.Ordinal) => 5,
            _ when name.StartsWith("июн", StringComparison.Ordinal) => 6,
            _ when name.StartsWith("июл", StringComparison.Ordinal) => 7,
            _ when name.StartsWith("август", StringComparison.Ordinal) => 8,
            _ when name.StartsWith("сентябр", StringComparison.Ordinal) => 9,
            _ when name.StartsWith("октябр", StringComparison.Ordinal) => 10,
            _ when name.StartsWith("ноябр", StringComparison.Ordinal) => 11,
            _ when name.StartsWith("декабр", StringComparison.Ordinal) => 12,
            _ => 0
        };
        if (month == 0) return false;
        var year = match.Groups["year"].Success
            ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture)
            : date.Value.Year;
        if (!int.TryParse(match.Groups["from"].Value, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(match.Groups["to"].Value, CultureInfo.InvariantCulture, out var end))
            return false;
        try
        {
            var from = new DateOnly(year, month, start);
            var to = new DateOnly(year, month, end);
            if (to < from) return false;
            if (!match.Groups["year"].Success && from > DateOnly.FromDateTime(date.Value).AddMonths(2))
            {
                from = from.AddYears(-1);
                to = to.AddYears(-1);
            }
            fromDate = from;
            toDate = to;
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
