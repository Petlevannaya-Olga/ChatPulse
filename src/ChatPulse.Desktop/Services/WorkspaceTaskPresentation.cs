using System.Text.Json;
using System.Text.RegularExpressions;
using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Extracts Instagram Reel and feed-post URLs for header actions, while preserving the
/// original imported message in SQLite. Telegram Custom Emoji offsets are UTF-16.
/// </summary>
public sealed record WorkspaceTaskPresentation(
    string Text, string CustomEmojiJson, string ReelUrl, string FeedPostUrl)
{
    private static readonly Regex INSTAGRAM_POST_URL = new(
        @"https?://(?:www\.)?instagram\.com/(?<kind>reel|p)/[A-Za-z0-9_-]+/?(?:\?[^\s<>\[\]()]+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    public static WorkspaceTaskPresentation Create(string? text, string? customEmojiJson)
    {
        text ??= string.Empty;
        customEmojiJson ??= string.Empty;
        var matches = INSTAGRAM_POST_URL.Matches(text).Cast<Match>()
            .Where(match => match.Success)
            .ToArray();
        if (matches.Length == 0)
            return new WorkspaceTaskPresentation(text, customEmojiJson, string.Empty, string.Empty);

        var reelUrl = matches.FirstOrDefault(match =>
            string.Equals(match.Groups["kind"].Value, "reel", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        var feedPostUrl = matches.FirstOrDefault(match =>
            string.Equals(match.Groups["kind"].Value, "p", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        // Remove only recognized Instagram post links, retaining the rest of the text.
        var visible = new System.Text.StringBuilder(text.Length);
        var position = 0;
        foreach (var match in matches)
        {
            visible.Append(text, position, match.Index - position);
            position = match.Index + match.Length;
        }
        visible.Append(text, position, text.Length - position);

        TelegramCustomEmoji[] entities;
        try { entities = JsonSerializer.Deserialize<TelegramCustomEmoji[]>(customEmojiJson) ?? []; }
        catch (JsonException) { entities = []; }

        if (entities.Length != 0)
        {
            var adjusted = new List<TelegramCustomEmoji>(entities.Length);
            foreach (var entity in entities)
            {
                if (entity.Offset < 0 || entity.Length <= 0 || entity.Offset > text.Length - entity.Length)
                    continue;
                // An emoji overlapping a removed URL cannot be remapped safely.
                if (matches.Any(match => entity.Offset < match.Index + match.Length &&
                                         entity.Offset + entity.Length > match.Index))
                    continue;
                var removedBefore = matches.Where(match => match.Index + match.Length <= entity.Offset)
                    .Sum(match => match.Length);
                adjusted.Add(entity with { Offset = entity.Offset - removedBefore });
            }
            customEmojiJson = JsonSerializer.Serialize(adjusted);
        }
        return new WorkspaceTaskPresentation(visible.ToString(), customEmojiJson, reelUrl, feedPostUrl);
    }
}
