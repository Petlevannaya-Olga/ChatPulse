namespace ChatPulse.Desktop.Services;

/// <summary>Only explicit, profile-specific page evidence may resolve a penalty suffix.</summary>
public static class InstagramProfilePageEvidence
{
    public static bool IsExplicitlyUnavailable(string pageText)
        => new[]
        {
            "Sorry, this page isn't available.",
            "Sorry, this page isn’t available.",
            "Эта страница недоступна",
            "Страница недоступна"
        }.Any(phrase => pageText.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    public static bool HasMatchingProfileHeading(string userName, string currentUrl,
        IEnumerable<string> visibleHeadings)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri) ||
            uri.Host is not ("www.instagram.com" or "instagram.com") ||
            !string.Equals(uri.AbsolutePath.Trim('/'), userName, StringComparison.OrdinalIgnoreCase))
            return false;
        return visibleHeadings.Any(heading =>
            string.Equals(heading.Trim().TrimStart('@'), userName, StringComparison.OrdinalIgnoreCase));
    }
}
