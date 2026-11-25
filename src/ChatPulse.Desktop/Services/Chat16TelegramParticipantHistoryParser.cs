using System.Text.RegularExpressions;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Telegram-only register used in "16 Пост чат тк". Repeated copies of the
/// register in one Telegram message are accepted only when the earlier copy
/// is an exact prefix of the last, complete copy.
/// </summary>
public sealed class Chat16TelegramParticipantHistoryParser : IChatParticipantHistoryParser
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex Number = new(@"^\s*(\d{1,4})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);
    // Match candidate links even when the username is invalid (e.g. t.me/e_).
    // Validation belongs to TryNormalizeProfile, so errors can cite the actual URL.
    private static readonly Regex TelegramLink = new(
        @"https?://(?:www\.)?t\.me/[^\s)\]>]+|(?<![\w/])@[A-Za-z0-9_]{1,64}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);

    // Version 2 records the two explicitly reviewed historical exceptions.
    // No arbitrary malformed links or duplicate identities are accepted.
    public ParticipantHistoryRule Rule { get; } = new(2, "Chat16TelegramRules",
        """{"historicalCorrections":["gelya-e_","neuro-sofia-duplicate"]}""");

    public bool IsCandidate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return false;
        try
        {
            var lines = Lines(text);
            return lines.Any(line => IsFounder(line)) &&
                   lines.Count(line => Number.IsMatch(line)) >= 1 &&
                   lines.Any(line => TelegramLink.IsMatch(line));
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public Task<ChatParticipantHistoryParseResult> ParseAsync(string? text, DateTime? sentAtUtc,
        IReadOnlySet<string> previouslySeenUsernames,
        Func<string, CancellationToken, Task<bool?>>? profileOpens,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCandidate(text))
            return Task.FromResult(Fail("Не обнаружен полный список участниц 16-го Telegram-чата."));
        try
        {
            var lines = Lines(text!);
            var starts = Enumerable.Range(0, lines.Length).Where(index => IsFounder(lines[index])).ToArray();
            var parsed = new List<Block>(starts.Length);
            for (var i = 0; i < starts.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = i + 1 < starts.Length ? starts[i + 1] : lines.Length;
                parsed.Add(ParseBlock(lines[starts[i]..end], allowIncompleteTail: i < starts.Length - 1));
            }
            var final = parsed[^1];
            if (final.Error is not null) return Task.FromResult(Fail(final.Error));
            foreach (var earlier in parsed.Take(parsed.Count - 1))
            {
                if (earlier.Error is not null)
                    return Task.FromResult(Fail("Повторный блок: " + earlier.Error));
                // A copied fragment may end mid-entry, but must agree with the
                // final copy on every fully specified row and administrator.
                if (earlier.Entries.Count > final.Entries.Count ||
                    earlier.Entries.Where(item => !final.Entries.Any(other =>
                        item.Position == other.Position &&
                        item.Category == other.Category &&
                        SameIdentity(item.SubscriptionProfileUrl, other.SubscriptionProfileUrl) &&
                        item.IsPenalty == other.IsPenalty)).Any())
                    return Task.FromResult(Fail("Повторяющиеся блоки списка противоречат друг другу."));
            }
            return Task.FromResult(new ChatParticipantHistoryParseResult(final.Entries, null));
        }
        catch (RegexMatchTimeoutException)
        {
            return Task.FromResult(Fail("Превышено время разбора списка Telegram."));
        }
    }

    private sealed record Block(IReadOnlyList<ParticipantHistoryEntry> Entries, string? Error = null);

    private static Block ParseBlock(string[] lines, bool allowIncompleteTail)
    {
        var result = new List<ParticipantHistoryEntry>();
        var numbered = new List<ParticipantHistoryEntry>();
        // Some Telegram messages repeat the numbered roster without repeating
        // the founder's header. Only an identical prefix can be discarded.
        var earlierNumberedCopies = new List<IReadOnlyList<ParticipantHistoryEntry>>();
        var penalty = false;
        var weekend = false;
        var number = 0;
        var pending = new List<string>();
        var section = string.Empty;
        string? sectionName = null;
        // A role is not a unique person: the chat can have several administrators.
        // Repeated identical role/profile pairs are harmless formatting copies.
        var administration = new List<ParticipantHistoryEntry>();
        string? reason = null;

        void FinishNumber(bool last = false)
        {
            if (number == 0 || reason is not null) return;
            var content = pending.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            var link = content.Select(ExtractLink).FirstOrDefault(value => value is not null);
            // The description may be a title, not necessarily "Name / Subject".
            // Do not synthesize a subject from an unstructured line.
            var name = content.FirstOrDefault(line => ExtractLink(line) is null &&
                !line.Contains("t.me/", StringComparison.OrdinalIgnoreCase) &&
                line.Any(char.IsLetter));
            if (link is null || name is null)
            {
                if (!allowIncompleteTail || !last)
                    reason = link is null
                        ? $"Запись №{number}: отсутствует Telegram-ссылка."
                        : $"Запись №{number}: отсутствует описание участницы.";
                return;
            }
            if (numbered.Count + 1 != number)
            {
                reason = $"Нарушена нумерация: ожидалась запись №{numbered.Count + 1}, получена №{number}.";
                return;
            }
            var sourceLink = link;
            string? resolutionNote = null;
            if (!ChatParticipantHistoryStore.TryNormalizeProfile(link, out _, out var canonical))
            {
                // Exception confirmed against later rosters for THIS chat:
                // historical #153/#166 give Gelya the truncated t.me/e_.
                // Do not infer matches for other names, descriptions or addresses.
                if (IsVerifiedGelyaAlias(link, name))
                {
                    canonical = "https://t.me/e_lkina";
                    resolutionNote = "Историческая ссылка https://t.me/e_ заменена на " +
                        "https://t.me/e_lkina по согласованному сопоставлению «Геля / Lifestyle» " +
                        "с более поздними списками. Личность не подтверждалась Telegram; " +
                        "исходная ссылка сохранена отдельно.";
                }
                else
                {
                    reason = $"Запись №{number}: некорректный Telegram-адрес «{link}».";
                    return;
                }
            }
            numbered.Add(new ParticipantHistoryEntry(canonical, canonical,
                "Обычный", IsWeekend: weekend, IsPenalty: penalty,
                Position: number, DisplayName: name,
                SourceSection: penalty ? "ШТРАФНОЙ ДЕНЬ" :
                    weekend ? "ВЫХОДНЫЕ ДНИ" : "УЧАСТНИКИ",
                SourceProfileUrl: resolutionNote is null ? null : sourceLink,
                ResolutionNote: resolutionNote));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.Contains("ШТРАФНОЙ ДЕНЬ", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("ШТРАФНЫЕ ДНИ", StringComparison.OrdinalIgnoreCase))
            {
                FinishNumber();
                number = 0; pending.Clear(); penalty = true; weekend = false;
                section = string.Empty; sectionName = null;
                continue;
            }
            if (line.Contains("ВЫХОДНЫЕ ДНИ", StringComparison.OrdinalIgnoreCase))
            {
                FinishNumber();
                number = 0; pending.Clear(); penalty = false; weekend = true;
                section = string.Empty; sectionName = null;
                continue;
            }
            var match = Number.Match(line);
            if (match.Success)
            {
                FinishNumber();
                if (reason is not null) break;
                var nextNumber = int.Parse(match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                if (nextNumber == 1 && numbered.Count > 0)
                {
                    // In historic messages #126/#137, a single "1" between
                    // 10 and 12 is a typo for 11, not a roster restart.
                    // Repair only this unambiguous pattern and only if the row
                    // has a new, valid identity. Other restarts remain strict.
                    var followingNumber = FindNextNumber(lines, i + 1);
                    var candidateLink = FindFollowingLink(lines, i + 1);
                    var isUniqueEleventh = numbered.Count == 10 &&
                        followingNumber == 12 && candidateLink is not null &&
                        ChatParticipantHistoryStore.TryNormalizeProfile(candidateLink,
                            out var candidateIdentity, out _) &&
                        !numbered.Any(item =>
                            ChatParticipantHistoryStore.TryNormalizeProfile(
                                item.SubscriptionProfileUrl, out var existingIdentity, out _) &&
                            string.Equals(candidateIdentity, existingIdentity, StringComparison.Ordinal));
                    if (isUniqueEleventh)
                        nextNumber = 11;
                    else
                    {
                        // A restart is accepted only if the new sequence is an
                        // identical, complete replacement of the previous one.
                        earlierNumberedCopies.Add(numbered.ToArray());
                        numbered.Clear();
                    }
                }
                number = nextNumber;
                pending.Clear(); section = string.Empty;
                sectionName = null;
                continue;
            }
            if (number > 0)
            {
                pending.Add(line);
                continue;
            }
            if (line.Contains("ДИРЕКТОР", StringComparison.OrdinalIgnoreCase))
            {
                section = "Директор";
                sectionName = null;
            }
            else if (line.Contains("АДМИНИСТРАТОР", StringComparison.OrdinalIgnoreCase))
            {
                section = "Администратор";
                sectionName = null;
            }
            else if (line.Contains("МОДЕРАТОР", StringComparison.OrdinalIgnoreCase))
            {
                section = "Модератор";
                sectionName = null;
            }
            else if (IsFounder(line))
            {
                section = "Основатель";
                sectionName = null;
            }
            else if (section.Length > 0)
            {
                var link = ExtractLink(line);
                if (link is not null)
                {
                    if (!ChatParticipantHistoryStore.TryNormalizeProfile(link, out _, out var canonical))
                        return new Block([], $"Некорректная Telegram-ссылка в разделе «{section}».");
                    if (!administration.Any(person =>
                        string.Equals(person.Category, section, StringComparison.OrdinalIgnoreCase) &&
                        SameIdentity(person.SubscriptionProfileUrl, canonical)))
                        administration.Add(new ParticipantHistoryEntry(canonical,
                            canonical, section, DisplayName: sectionName ?? section,
                            SourceSection: section));
                    section = string.Empty;
                    sectionName = null;
                }
                else if (!line.Contains("ЛИНЕЙКИ", StringComparison.OrdinalIgnoreCase) &&
                         line.Any(char.IsLetter) &&
                         !line.StartsWith("⚡", StringComparison.Ordinal) &&
                         !line.StartsWith("💘", StringComparison.Ordinal))
                    sectionName = line.Trim('*', ' ');
            }
        }
        FinishNumber(last: true);
        if (reason is not null) return new Block([], reason);
        foreach (var previous in earlierNumberedCopies)
        {
            if (previous.Count > numbered.Count || previous.Any(item =>
                !numbered.Any(current => current.Position == item.Position &&
                    current.IsPenalty == item.IsPenalty &&
                    SameIdentity(current.SubscriptionProfileUrl, item.SubscriptionProfileUrl))))
                return new Block([], "Повторная нумерация с №1: записи не совпадают " +
                    "с последним полным блоком. Нужна проверка исходного сообщения.");
        }
        var requiredRoles = new[] { "Директор", "Администратор", "Модератор" };
        if (!requiredRoles.All(role => administration.Any(person =>
                string.Equals(person.Category, role, StringComparison.OrdinalIgnoreCase))))
            return new Block([], "Отсутствует Telegram-ссылка директора, администратора или модератора.");
        if (numbered.Count < 1) return new Block([], "В списке нет пронумерованных участниц.");
        foreach (var role in requiredRoles)
            result.AddRange(administration.Where(person =>
                string.Equals(person.Category, role, StringComparison.OrdinalIgnoreCase)));
        result.AddRange(numbered);
        foreach (var duplicates in result.GroupBy(item =>
        {
            ChatParticipantHistoryStore.TryNormalizeProfile(item.SubscriptionProfileUrl,
                out var identity, out _);
            return identity;
        }, StringComparer.Ordinal).Where(group => group.Count() > 1).ToArray())
        {
            var rows = duplicates.ToArray();
            if (TryMergeKnownNeuroSofiaDuplicate(duplicates.Key, rows, out var merged))
            {
                result.RemoveAll(item => rows.Contains(item));
                result.Add(merged);
                continue;
            }
            var locations = string.Join(", ", rows.Select(item =>
                item.Position.HasValue
                    ? $"№{item.Position} «{item.DisplayName}» ({item.SourceSection})"
                    : $"{item.Category} «{item.DisplayName}»"));
            return new Block([], $"Один Telegram-адрес указан несколько раз: " +
                $"{duplicates.Key} ({locations}). Нужна проверка исходного списка.");
        }
        // Preserve the section ordering after replacing two source rows with one.
        return new Block(result.Where(item => !item.Position.HasValue)
            .Concat(result.Where(item => item.Position.HasValue)
                .OrderBy(item => item.Position)).ToArray());
    }

    private static bool IsVerifiedGelyaAlias(string link, string displayName)
    {
        if (!string.Equals(link.TrimEnd('/'), "https://t.me/e_", StringComparison.OrdinalIgnoreCase))
            return false;
        var normalizedName = Regex.Replace(displayName.Trim(), @"\s+", " ",
            RegexOptions.CultureInvariant, Timeout);
        return string.Equals(normalizedName, "Геля / Lifestyle", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMergeKnownNeuroSofiaDuplicate(string identity,
        IReadOnlyList<ParticipantHistoryEntry> entries, out ParticipantHistoryEntry merged)
    {
        merged = null!;
        if (!string.Equals(identity, "tg:neuro_sofia_art", StringComparison.Ordinal) ||
            entries.Count != 2 || entries.Any(item => !item.Position.HasValue ||
                item.Category != "Обычный" || item.IsWeekend))
            return false;

        // The reviewed source rosters contain Elena and Alena with the SAME link.
        // This is an explicit user-approved merge, NOT evidence of shared identity.
        var descriptions = entries.Select(item => Regex.Replace(
            (item.DisplayName ?? string.Empty).Trim(), @"\s+", " ",
            RegexOptions.CultureInvariant, Timeout).Replace('ё', 'е').Replace('Ё', 'Е'))
            .ToArray();
        if (!descriptions.Contains("Елена / Lifestyle", StringComparer.OrdinalIgnoreCase) ||
            !descriptions.Contains("Алена / Lifestyle", StringComparer.OrdinalIgnoreCase) ||
            entries[0].Position == entries[1].Position)
            return false;

        var penalty = entries.Any(item => item.IsPenalty);
        var chosen = penalty ? entries.First(item => item.IsPenalty) : entries[0];
        var sourceRows = string.Join("; ", entries.OrderBy(item => item.Position).Select(item =>
            $"№{item.Position} «{item.DisplayName}» ({item.SourceSection})"));
        merged = chosen with
        {
            DisplayName = "Елена / Алена",
            IsPenalty = penalty,
            SourceSection = penalty ? "ШТРАФНОЙ ДЕНЬ" : "УЧАСТНИКИ",
            ResolutionNote = $"Профиль @neuro_sofia_art в исходном списке " +
                $"повторяется: {sourceRows}. По решению пользователя записи " +
                "объединены в одну; принадлежность профиля конкретному человеку " +
                "не установлена. Штрафной статус сохранён, если он был хотя бы " +
                "в одной исходной записи."
        };
        return true;
    }

    private static int? FindNextNumber(string[] lines, int start)
    {
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (IsSectionBoundary(line)) return null;
            var match = Number.Match(line);
            if (match.Success)
                return int.Parse(match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static string? FindFollowingLink(string[] lines, int start)
    {
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (IsSectionBoundary(line) || Number.IsMatch(line)) return null;
            var link = ExtractLink(line);
            if (link is not null) return link;
        }
        return null;
    }

    private static bool IsSectionBoundary(string line) =>
        IsFounder(line) ||
        line.Contains("ШТРАФНОЙ ДЕНЬ", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("ШТРАФНЫЕ ДНИ", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("ВЫХОДНЫЕ ДНИ", StringComparison.OrdinalIgnoreCase);

    private static bool SameIdentity(string left, string right) =>
        ChatParticipantHistoryStore.TryNormalizeProfile(left, out var leftId, out _) &&
        ChatParticipantHistoryStore.TryNormalizeProfile(right, out var rightId, out _) &&
        string.Equals(leftId, rightId, StringComparison.Ordinal);

    private static ChatParticipantHistoryParseResult Fail(string error) => new(null, error);
    private static bool IsFounder(string value) =>
        value.Contains("ОСНОВАТЕЛЬ КОМПАНИИ", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractLink(string text)
    {
        var match = TelegramLink.Match(text);
        if (!match.Success) return null;
        return match.Value[0] == '@' ? "https://t.me/" + match.Value[1..] : match.Value.TrimEnd('/');
    }

    private static string[] Lines(string text) => text.Replace("\r", string.Empty,
        StringComparison.Ordinal).Replace("**", string.Empty, StringComparison.Ordinal)
        .Replace("\\", string.Empty, StringComparison.Ordinal)
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
