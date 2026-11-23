using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

public enum InstagramTaskCheckStatus { CONFIRMED, INCONCLUSIVE }

public sealed record InstagramTaskCheckResult(InstagramTaskCheckStatus Status, string Detail)
{
    public static InstagramTaskCheckResult Confirmed(string detail) =>
        new(InstagramTaskCheckStatus.CONFIRMED, detail);
    public static InstagramTaskCheckResult Inconclusive(string detail) =>
        new(InstagramTaskCheckStatus.INCONCLUSIVE, detail);
}

public sealed record InstagramTaskVerificationResult(
    string UserName, string PostUrl, InstagramTaskCheckResult? Like,
    InstagramTaskCheckResult? Comment,
    InstagramTaskCheckResult? Save = null, InstagramTaskCheckResult? Repost = null)
{
    // A check not requested is null; an inconclusive check never hides a task.
    public bool CanHide => (Like is not null || Comment is not null || Save is not null || Repost is not null) &&
        (Like is null || Like.Status == InstagramTaskCheckStatus.CONFIRMED) &&
        (Comment is null || Comment.Status == InstagramTaskCheckStatus.CONFIRMED) &&
        (Save is null || Save.Status == InstagramTaskCheckStatus.CONFIRMED) &&
        (Repost is null || Repost.Status == InstagramTaskCheckStatus.CONFIRMED);
}

public static class InstagramTaskVerificationRules
{
    public const int MAX_LIKE_SCROLLS = 10;

    // Conservative automatic selection: ambiguous phrases remain unchecked.
    // The operator can correct these suggestions before checking.
    public static (bool Like, bool Comment, bool Save, bool Repost) SuggestChecks(string? taskText)
    {
        if (string.IsNullOrWhiteSpace(taskText)) return (true, false, false, false);
        var text = NormalizeActionText(taskText);
        return (true, // A like is mandatory for every post task, even without an explicit request.
            ContainsAction(text, @"(?:коммент(?:арий|арии|ировать|ы)?|коментар(?:ий|ия|ию|ием|ии|иев|иях|иями)|комент|напишите\s+слово)", optionalComment: true),
            ContainsAction(text, @"(?:сохран(?:ить|ите|ение|ения)|закладк[уаие])"),
            ContainsAction(text, @"(?:репост(?:нуть|ите|а|ы)?|перепост(?:ить|ите)?)"));
    }

    // Telegram text may contain invisible formatting characters or Latin letters
    // visually indistinguishable from Cyrillic. Normalize only for rule matching;
    // the original message remains unchanged in SQLite and on the task card.
    private static string NormalizeActionText(string text)
    {
        var canonical = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var clean = new StringBuilder(canonical.Length);
        foreach (var character in canonical)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format)
                continue;
            clean.Append(character);
        }

        return Regex.Replace(clean.ToString(), @"[\p{L}]+", match =>
        {
            var word = match.Value;
            if (!word.Any(character => character is >= 'а' and <= 'я' or 'ё'))
                return word;

            // Replace confusables only in words already containing Cyrillic letters.
            var result = new StringBuilder(word.Length);
            foreach (var character in word)
            {
                result.Append(character switch
                {
                    'a' => 'а', 'c' => 'с', 'e' => 'е', 'o' => 'о',
                    'p' => 'р', 'x' => 'х', 'y' => 'у',
                    _ => character
                });
            }
            return result.ToString();
        }, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
    }

    private static bool ContainsAction(string text, string pattern, bool optionalComment = false)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(text,
            $@"(?<![\p{{L}}]){pattern}(?![\p{{L}}])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(150));
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var start = Math.Max(0, match.Index - 32);
            var prefix = text[start..match.Index];
            var suffix = text[(match.Index + match.Length)..Math.Min(text.Length, match.Index + match.Length + 24)];
            // Exclude common negative instructions, including negations after nouns.
            if (System.Text.RegularExpressions.Regex.IsMatch(suffix,
                @"^\s*(?:не\s+нуж(?:ен|на|но|ны)|не\s+требуется|не\s+обязател(?:ен|ьно))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(150))) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(prefix,
                @"(?:не\s+надо|не\s+нужно|без|не\s+ставить|не\s+делать|не\s+требуется)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(150))) continue;
            if (optionalComment && IsOptionalComment(text, match)) continue;
            return true;
        }
        return false;
    }

    // A comment marked "по желанию" is not an automatic requirement.
    // Keep matching local to the comment's line to avoid reading optional
    // instructions belonging to a different task action.
    private static bool IsOptionalComment(string text, Match comment)
    {
        var lineStart = text.LastIndexOf('\n', comment.Index);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = text.IndexOf('\n', comment.Index + comment.Length);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;

        var before = text[lineStart..comment.Index];
        var lastSeparator = Math.Max(before.LastIndexOf(','), before.LastIndexOf(';'));
        before = before[(lastSeparator + 1)..];
        var optionalBefore = Regex.IsMatch(before,
            @"(?:по\s+желанию|не\s*обязательно|опционально)\s*[-—–:]?\s*$",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
        if (optionalBefore) return true;

        var after = text[(comment.Index + comment.Length)..lineEnd];
        // If another action appears first, its optional instruction should not
        // silently make this comment optional.
        var nextAction = Regex.Match(after,
            @"(?<![\p{L}])(?:лайк|репост|сохран|подписк|отписк)(?![\p{L}])",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
        if (nextAction.Success) after = after[..nextAction.Index];

        return Regex.IsMatch(after,
            @"(?:по\s+желанию|не\s*обязател(?:ьно|ен|ьна|ьны|ьный)|опциональн[\p{L}]*|если\s+хотите)",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
    }

    // The likes count is the sibling of the span wrapping the Like/Unlike action.
    // Do not click the preceding heart button: it changes the account's reaction.
    // Do not rely on the count text (it can change) or Instagram's generated CSS classes.
    public const string LIKES_COUNTER_AFTER_HEART_XPATH =
        "//svg[@aria-label='Нравится' or @aria-label='Больше не нравится' " +
        "or @aria-label='Like' or @aria-label='Unlike']" +
        "/ancestor::div[@role='button'][1]" +
        "/ancestor::span[1]" +
        "/following-sibling::div[@role='button'][1]";


    public static bool TryGetPostUrl(string? input, out string postUrl)
    {
        postUrl = string.Empty;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("www.instagram.com" or "instagram.com")) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts[0] is not ("p" or "reel" or "tv") ||
            parts[1].Length is < 5 or > 60 || !parts[1].All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) return false;
        postUrl = $"https://www.instagram.com/{parts[0]}/{parts[1]}/";
        return true;
    }

    /// <summary>
    /// Matches a textual likes counter, not a heart/Like action button.
    /// Presence of a counter alone does not guarantee that Instagram exposes the list.
    /// </summary>
    public static bool IsLikesCounterLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || !label.Any(char.IsDigit)) return false;
        return label.Contains("likes", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("отмет", StringComparison.OrdinalIgnoreCase) &&
            label.Contains("нравится", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLikesDialogHeading(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading)) return false;
        var normalized = heading.Trim().ToLowerInvariant();
        return normalized is "likes" or "нравится" or "отметки «нравится»" or "отметки \"нравится\""
            or "отметки нравится";
    }

    public static bool IsExactProfileLink(string? href, string userName)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
            uri.Host is not ("www.instagram.com" or "instagram.com")) return false;
        return string.Equals(uri.AbsolutePath.Trim('/'),
            InstagramUserName.Normalize(userName), StringComparison.OrdinalIgnoreCase);
    }

    public static InstagramTaskVerificationResult Combine(string user, string url,
        InstagramTaskCheckResult? like, InstagramTaskCheckResult? comment) =>
        new(InstagramUserName.Normalize(user), url, like, comment);
}
