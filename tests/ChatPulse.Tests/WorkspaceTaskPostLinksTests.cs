using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Views.Workspace;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTaskPostLinksTests
{
    private const string POST = "https://t.me/nejnost_2022/976";
    private const string TASK = "https://t.me/c/1234/500";

    [Fact]
    public void TelegramPostAndSourceTaskHaveDistinctButtonsAndPostUrlIsNotInText()
    {
        var source = $"Всем привет!\n\n{POST}\n\nРеакции и комментарий";
        var task = new WorkspaceDetectedTask(500, source, "", SourceUrl: TASK);
        var card = new WorkspaceTaskCard(task);

        Assert.DoesNotContain(POST, card.Description);
        Assert.Contains("Реакции и комментарий", card.Description);
        var post = Assert.Single(card.AdditionalActionLinks);
        Assert.Equal("↗  Пост", post.Text);
        Assert.Equal(POST, post.Url);
        Assert.Equal(TASK, card.SourceUrl);
        Assert.Equal(source, task.Text);
    }

    [Fact]
    public void ALinkToTheOriginalTaskIsNotRepeatedAsAnAdditionalPost()
    {
        var card = new WorkspaceTaskCard(new WorkspaceDetectedTask(
            500, $"Задание\n{TASK}", "", SourceUrl: TASK));
        Assert.Empty(card.AdditionalActionLinks);
        Assert.True(card.HasSourceUrl);
    }

    [Fact]
    public void UnsupportedLinksAreKeptInTextInsteadOfCreatingUnclickableButtons()
    {
        const string url = "https://example.org/article/123";
        var card = new WorkspaceTaskCard(new WorkspaceDetectedTask(500,
            $"Ссылка: {url}", "", SourceUrl: TASK));
        Assert.Contains(url, card.Description);
        Assert.Empty(card.AdditionalActionLinks);
    }

    [Fact]
    public void InstagramPostLinksRemainDedicatedButtonsAndAreRemovedFromBody()
    {
        const string link = "https://www.instagram.com/reel/DfT76Gxugio/";
        var card = new WorkspaceTaskCard(new WorkspaceDetectedTask(
            501, $"Лайк\n{link}\nКомментарий", link, SourceUrl: TASK));
        Assert.True(card.HasReelUrl);
        Assert.Equal(link, card.ReelUrl);
        Assert.DoesNotContain(link, card.Description);
        Assert.Empty(card.AdditionalActionLinks);
    }
}
