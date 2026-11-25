using System.Text.RegularExpressions;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Identifies direct Instagram Stories references. A profile URL is permitted next
/// to the story; a feed post/Reel makes the message a different (mixed) task.
/// A story is identified by both its author and its numeric media ID.
/// </summary>
public static class WorkspaceStoryTaskRules
{
    private static readonly Regex InstagramUserNamePattern = new(
        @"^[A-Za-z0-9._]{1,30}$", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(150));

    public static bool TryNormalizeStoryUrl(string? input, out string storyUrl)
    {
        storyUrl = string.Empty;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !IsInstagramHost(uri.Host)) return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 ||
            !segments[0].Equals("stories", StringComparison.OrdinalIgnoreCase) ||
            !InstagramUserNamePattern.IsMatch(segments[1]) ||
            segments[2].Length is < 8 or > 32 ||
            !segments[2].All(char.IsAsciiDigit)) return false;

        storyUrl = $"https://www.instagram.com/stories/{segments[1].ToLowerInvariant()}/{segments[2]}/";
        return true;
    }

    public static bool IsSameStoryUrl(string? actual, string expected)
    {
        if (!TryNormalizeStoryUrl(actual, out var actualUrl) ||
            !TryNormalizeStoryUrl(expected, out var expectedUrl)) return false;
        return string.Equals(actualUrl, expectedUrl, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> GetStoryOnlyUrls(string? message, string? storedUrl)
    {
        var urls = WorkspaceTaskLinks.Split(message)
            .Select(part => part.Url)
            .Concat(new[] { storedUrl })
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToArray();

        if (urls.Any(IsFeedPublicationUrl)) return [];
        var stories = new List<string>();
        foreach (var url in urls)
            if (TryNormalizeStoryUrl(url, out var storyUrl) &&
                !stories.Contains(storyUrl, StringComparer.OrdinalIgnoreCase))
                stories.Add(storyUrl);
        return stories;
    }

    public static bool IsStoryOnlyTask(string? message, string? storedUrl) =>
        GetStoryOnlyUrls(message, storedUrl).Count > 0;

    private static bool IsFeedPublicationUrl(string? input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            !IsInstagramHost(uri.Host)) return false;
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return path.Length >= 2 &&
            (path[0].Equals("p", StringComparison.OrdinalIgnoreCase) ||
             path[0].Equals("reel", StringComparison.OrdinalIgnoreCase) ||
             path[0].Equals("reels", StringComparison.OrdinalIgnoreCase) ||
             path[0].Equals("tv", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInstagramHost(string host) =>
        host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("www.instagram.com", StringComparison.OrdinalIgnoreCase);
}
