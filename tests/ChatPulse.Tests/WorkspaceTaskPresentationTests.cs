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
        Assert.Equal(1, entities[0].Offset);
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
}
