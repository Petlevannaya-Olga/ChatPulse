using System.Text.RegularExpressions;

namespace ChatPulse.Desktop.Services;

public sealed record WorkspaceTaskTextPart(string Text, string? Url = null);

/// <summary>Splits task body into normal text and safe, short-labelled HTTP(S) links.</summary>
public static class WorkspaceTaskLinks
{
    private static readonly Regex URLS = new(@"https?://[^\s<>\[\]""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public static IReadOnlyList<WorkspaceTaskTextPart> Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var parts = new List<WorkspaceTaskTextPart>();
        var position = 0;
        foreach (Match match in URLS.Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '}', '»');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                string.IsNullOrWhiteSpace(uri.Host)) continue;
            if (match.Index > position)
                parts.Add(new WorkspaceTaskTextPart(text[position..match.Index]));
            var label = uri.Host.EndsWith("instagram.com", StringComparison.OrdinalIgnoreCase)
                ? (uri.AbsolutePath.StartsWith("/reel/", StringComparison.OrdinalIgnoreCase)
                    ? "Открыть Reel" : uri.AbsolutePath.StartsWith("/p/", StringComparison.OrdinalIgnoreCase)
                        ? "Открыть публикацию" : "Открыть Instagram")
                : uri.Host.EndsWith("t.me", StringComparison.OrdinalIgnoreCase) ? "Открыть Telegram" : "Открыть ссылку";
            parts.Add(new WorkspaceTaskTextPart(label, candidate));
            position = match.Index + candidate.Length;
        }
        if (position < text.Length)
            parts.Add(new WorkspaceTaskTextPart(text[position..]));
        return parts;
    }
}
