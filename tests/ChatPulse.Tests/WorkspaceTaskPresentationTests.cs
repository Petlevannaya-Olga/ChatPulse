using System.Text.Json;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTaskPresentationTests
{
    [Fact]
    public void ExtractsReelAndPostWithoutRemovingOtherTextOrLinks()
    {
        const string reel = "https://www.instagram.com/reel/abc123/?r=1";
        const string post = "https://www.instagram.com/p/def456/";
        var result = WorkspaceTaskPresentation.Create($"VIP LITE\n{reel}\n{post}\nПодписка 🩷\nhttps://t.me/example", "");
        Assert.Equal(reel, result.ReelUrl);
        Assert.Equal(post, result.FeedPostUrl);
        Assert.DoesNotContain(reel, result.Text);
        Assert.DoesNotContain(post, result.Text);
        Assert.Contains("Подписка 🩷", result.Text);
        Assert.Contains("https://t.me/example", result.Text);
    }

    [Fact]
    public void RemapsCustomEmojiOffsetsAfterRemovingUrls()
    {
        const string reel = "https://instagram.com/reel/abc123/";
        var text = $"{reel}\n⚡ПОДПИСКА";
        var entity = new TelegramCustomEmoji(reel.Length + 1, 1, 123);
        var result = WorkspaceTaskPresentation.Create(text, JsonSerializer.Serialize(new[] { entity }));
        var entities = JsonSerializer.Deserialize<TelegramCustomEmoji[]>(result.CustomEmojiJson);
        Assert.NotNull(entities);
        Assert.Single(entities);
        Assert.Equal(0, entities[0].Offset);
        Assert.Equal('⚡', result.Text[entities[0].Offset]);
    }

    [Fact]
    public void LeavesNonPostProfileAndMessagesWithoutLinksUntouched()
    {
        const string text = "Подпишитесь: https://www.instagram.com/someone/";
        var result = WorkspaceTaskPresentation.Create(text, "");
        Assert.Equal(text, result.Text);
        Assert.Empty(result.ReelUrl);
        Assert.Empty(result.FeedPostUrl);
    }
    [Fact]
    public void RemovesLeadingTrailingAndRepeatedBlankLinesInEveryMessage()
    {
        const string original = "\r\n \r\nПервый абзац\r\n\r\n \r\n\r\nВторой абзац\r\n\r\n";
        var result = WorkspaceTaskPresentation.Create(original, string.Empty);
        Assert.Equal("Первый абзац\n\nВторой абзац", result.Text);
    }

    [Fact]
    public void RemovesEmptyLinesLeftByExtractedInstagramUrls()
    {
        const string reel = "https://www.instagram.com/reel/abc123/";
        const string post = "https://www.instagram.com/p/abc123/";
        var text = $"\n\nVIP LITE\n\n{reel}\n\n\n{post}\n\n\nПОДПИСКА\n\n";
        var result = WorkspaceTaskPresentation.Create(text, string.Empty);
        Assert.Equal("VIP LITE\n\nПОДПИСКА", result.Text);
        Assert.Equal(reel, result.ReelUrl);
        Assert.Equal(post, result.FeedPostUrl);
    }

    [Fact]
    public void RemapsCustomEmojiOffsetsAfterRemovingEmptyLines()
    {
        const string original = "\n\nСтрока\n\n\n⚡ПОДПИСКА\n\n";
        var offset = original.IndexOf('⚡');
        var entity = new TelegramCustomEmoji(offset, 1, 123);
        var result = WorkspaceTaskPresentation.Create(original, JsonSerializer.Serialize(new[] { entity }));
        var emojis = JsonSerializer.Deserialize<TelegramCustomEmoji[]>(result.CustomEmojiJson);
        Assert.NotNull(emojis);
        Assert.Single(emojis);
        Assert.Equal("Строка\n\n⚡ПОДПИСКА", result.Text);
        Assert.Equal(result.Text.IndexOf('⚡'), emojis[0].Offset);
    }

    [Fact]
    public void PreservesEmojiOffsetsAfterBothUrlAndEmptyLineRemoval()
    {
        const string reel = "https://instagram.com/reel/a1b2c3/";
        var original = $"\n{reel}\n\n\n⚡ПОДПИСКА\n\n";
        var entity = new TelegramCustomEmoji(original.IndexOf('⚡'), 1, 123);
        var result = WorkspaceTaskPresentation.Create(original, JsonSerializer.Serialize(new[] { entity }));
        var emojis = JsonSerializer.Deserialize<TelegramCustomEmoji[]>(result.CustomEmojiJson);
        Assert.Equal("⚡ПОДПИСКА", result.Text);
        Assert.NotNull(emojis);
        Assert.Single(emojis);
        Assert.Equal(0, emojis[0].Offset);
    }

    [Fact]
    public void KeepsSingleLineBreakAndMeaningfulSpaces()
    {
        const string original = "Название\n  Пункт с отступом\nСледующий пункт";
        Assert.Equal(original, WorkspaceTaskPresentation.Create(original, string.Empty).Text);
    }

    [Theory]
    [InlineData("или")]
    [InlineData("ИЛИ")]
    [InlineData("Или")]
    public void RemovesStandaloneOrBetweenExtractedReelAndPost(string conjunction)
    {
        const string reel = "https://www.instagram.com/reel/abc123/";
        const string post = "https://www.instagram.com/p/def456/";
        var original = $"VIP LITE\n\n{reel}\n\n{conjunction}\n\n{post}\n\nПОДПИСКА";

        var result = WorkspaceTaskPresentation.Create(original, string.Empty);

        Assert.Equal("VIP LITE\n\nПОДПИСКА", result.Text);
        Assert.Equal(reel, result.ReelUrl);
        Assert.Equal(post, result.FeedPostUrl);
    }

    [Fact]
    public void PreservesOrInTaskInstructionsAndWhenOnlyOnePostLinkExists()
    {
        const string reel = "https://www.instagram.com/reel/abc123/";
        const string post = "https://www.instagram.com/p/def456/";
        var instructions = $"Поставить лайк или написать комментарий\n{reel}\n{post}\nМожно сохранить или переслать";
        var result = WorkspaceTaskPresentation.Create(instructions, string.Empty);
        Assert.Equal("Поставить лайк или написать комментарий\n\nМожно сохранить или переслать", result.Text);

        var standalone = WorkspaceTaskPresentation.Create($"{reel}\nили\nПроверить текст", string.Empty);
        Assert.Equal("или\nПроверить текст", standalone.Text);
    }

    [Fact]
    public void RemapsCustomEmojiAfterRemovingOrBetweenLinks()
    {
        const string reel = "https://www.instagram.com/reel/abc123/";
        const string post = "https://www.instagram.com/p/def456/";
        var original = $"{reel}\nИли\n{post}\n\n⚡ПОДПИСКА";
        var emoji = new TelegramCustomEmoji(original.IndexOf('⚡'), 1, 123);

        var result = WorkspaceTaskPresentation.Create(original, JsonSerializer.Serialize(new[] { emoji }));
        var entities = JsonSerializer.Deserialize<TelegramCustomEmoji[]>(result.CustomEmojiJson);

        Assert.Equal("⚡ПОДПИСКА", result.Text);
        Assert.NotNull(entities);
        Assert.Single(entities);
        Assert.Equal(0, entities[0].Offset);
    }

}
