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
        var reelUrl = matches.FirstOrDefault(match =>
            string.Equals(match.Groups["kind"].Value, "reel", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        var feedPostUrl = matches.FirstOrDefault(match =>
            string.Equals(match.Groups["kind"].Value, "p", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        // Build the displayed text without changing the original Telegram message.
        // Every kept UTF-16 code unit remembers its original offset so that custom
        // emoji entities can be remapped after URL removal and whitespace cleanup.
        var removed = new bool[text.Length];
        foreach (var match in matches)
            for (var index = match.Index; index < match.Index + match.Length; index++)
                removed[index] = true;

        // A standalone "или" between alternative Instagram Reel/Post URLs is
        // part of the links' presentation, not of the task's instructions.
        // Remove it only when the entire gap between consecutive URLs is
        // whitespace and this conjunction. Ordinary text remains untouched.
        for (var matchIndex = 0; matchIndex < matches.Length - 1; matchIndex++)
        {
            var first = matches[matchIndex];
            var second = matches[matchIndex + 1];
            if (string.Equals(first.Groups["kind"].Value, second.Groups["kind"].Value,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var gapStart = first.Index + first.Length;
            var gap = text.AsSpan(gapStart, second.Index - gapStart);
            if (!gap.Trim().Equals("или".AsSpan(), StringComparison.OrdinalIgnoreCase))
                continue;

            for (var index = gapStart; index < second.Index; index++)
                if (!char.IsWhiteSpace(text[index]))
                    removed[index] = true;
        }

        var visibleCharacters = new List<(char Character, int OriginalOffset)>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (removed[index])
                continue;

            // Normalize line endings for presentation without touching SQLite.
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    continue;
                visibleCharacters.Add(('\n', index));
            }
            else
                visibleCharacters.Add((text[index], index));
        }

        var lines = new List<List<(char Character, int OriginalOffset)>>();
        var line = new List<(char Character, int OriginalOffset)>();
        foreach (var character in visibleCharacters)
        {
            if (character.Character == '\n')
            {
                lines.Add(line);
                line = new List<(char Character, int OriginalOffset)>();
            }
            else
                line.Add(character);
        }
        lines.Add(line);

        var builder = new System.Text.StringBuilder(text.Length);
        var offsetMap = Enumerable.Repeat(-1, text.Length).ToArray();
        var hasContent = false;
        var blankLinesSinceContent = 0;
        foreach (var currentLine in lines)
        {
            if (currentLine.All(character => char.IsWhiteSpace(character.Character)))
            {
                if (hasContent)
                    blankLinesSinceContent++;
                continue;
            }

            if (hasContent)
            {
                builder.Append('\n');
                if (blankLinesSinceContent > 0)
                    builder.Append('\n');
            }

            // Keep meaningful indentation and spacing within a content line.
            foreach (var character in currentLine)
            {
                offsetMap[character.OriginalOffset] = builder.Length;
                builder.Append(character.Character);
            }
            hasContent = true;
            blankLinesSinceContent = 0;
        }

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

                var newOffset = offsetMap[entity.Offset];
                if (newOffset < 0)
                    continue;

                // All UTF-16 code units belonging to an emoji must survive together.
                var intact = true;
                for (var position = 1; position < entity.Length; position++)
                {
                    if (offsetMap[entity.Offset + position] != newOffset + position)
                    {
                        intact = false;
                        break;
                    }
                }
                if (intact)
                    adjusted.Add(entity with { Offset = newOffset });
            }
            customEmojiJson = JsonSerializer.Serialize(adjusted);
        }
        return new WorkspaceTaskPresentation(builder.ToString(), customEmojiJson, reelUrl, feedPostUrl);
    }
}
