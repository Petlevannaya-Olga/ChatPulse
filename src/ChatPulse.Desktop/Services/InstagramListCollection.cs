using System.Globalization;
using System.Text.RegularExpressions;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>Snapshot of a list visible in Instagram. An incomplete snapshot cannot prove absence.</summary>
public sealed record InstagramListCollection(
    IReadOnlySet<string> UserNames,
    int? ExpectedCount,
    bool ReachedScrollEnd,
    string? Diagnostics = null)
{
    public bool IsComplete => ReachedScrollEnd && ExpectedCount is { } expected && UserNames.Count == expected;

    public string Describe(string label) =>
        $"{label}: получено {UserNames.Count}, " +
        (ExpectedCount is { } count ? $"показано в профиле {count}" : "точное количество неизвестно") +
        (IsComplete ? "; список полный." : "; полнота не подтверждена — отсутствие аккаунта нельзя считать отпиской.");
}

public static partial class InstagramListCollectionParser
{
    // Instagram may show abbreviated values (1.2K, 1,2 тыс.). They are NOT exact counts.
    [GeneratedRegex(@"^\s*([0-9]+(?:[.,\s][0-9]{3})*)\s*(?:followers?|following|подписчик(?:ов|а|и)?|подписок|подписки|подписка)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ExactCountPattern();

    public static int? ParseExactCount(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var match = ExactCountPattern().Match(label);
        if (!match.Success) return null;
        var digits = string.Concat(match.Groups[1].Value.Where(char.IsAsciiDigit));
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count : null;
    }

    public static bool IsListUrl(string? href, string target, string kind)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var url) ||
            url.Host is not "www.instagram.com" and not "instagram.com") return false;
        return url.AbsolutePath.TrimEnd('/').Equals($"/{target}/{kind}", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlySet<string> ParseProfileLinks(IEnumerable<string> paths)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var url) ||
                url.Host is not "www.instagram.com" and not "instagram.com") continue;
            var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 1 || segments[0] is "accounts" or "explore" or "direct" or "reels" or "stories" or "about" or "developer" or "privacy") continue;
            try { names.Add(InstagramUserName.Normalize(segments[0])); }
            catch (ArgumentException) { /* Not a user profile. */ }
        }
        return names;
    }
}
