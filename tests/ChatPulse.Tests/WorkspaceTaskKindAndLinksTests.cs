using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTaskKindAndLinksTests
{
    [Theory]
    [InlineData("ПОДПИСКА 💜 https://instagram.com/user", WorkspaceTaskKind.SUBSCRIBE)]
    [InlineData("⚡ ОТПИСКА ⚡ https://instagram.com/user", WorkspaceTaskKind.UNSUBSCRIBE)]
    [InlineData("О💔Т💔П💔И💔С💔К💔А", WorkspaceTaskKind.UNSUBSCRIBE)]
    [InlineData("о т п и с к а", WorkspaceTaskKind.UNSUBSCRIBE)]
    [InlineData("Моя публикация https://instagram.com/p/example", WorkspaceTaskKind.TASK)]
    [InlineData("", WorkspaceTaskKind.TASK)]
    public void ClassifiesTaskTextForPresentation(string text, WorkspaceTaskKind expected)
        => Assert.Equal(expected, WorkspaceTaskKindDetector.Detect(text));

    [Fact]
    public void UnsubscribeTakesPriorityOverSubscribe()
        => Assert.Equal(WorkspaceTaskKind.UNSUBSCRIBE,
            WorkspaceTaskKindDetector.Detect("Подписка, потом отписка"));

    [Fact]
    public void ConvertsLinksToShortLabelsWithoutChangingTheirTargets()
    {
        var source = "Ссылка: https://www.instagram.com/profile_x/; чат https://t.me/example.";
        var parts = WorkspaceTaskLinks.Split(source);
        Assert.Contains(parts, part => part.Text == "Открыть Instagram" &&
            part.Url == "https://www.instagram.com/profile_x/");
        Assert.Contains(parts, part => part.Text == "Открыть Telegram" &&
            part.Url == "https://t.me/example");
        Assert.Contains(parts, part => part.Text == "; чат " && part.Url is null);
        Assert.EndsWith(".", parts[^1].Text);
    }

    [Fact]
    public void PlainTextIsNotTurnedIntoLink()
    {
        var part = Assert.Single(WorkspaceTaskLinks.Split("Подписка без URL"));
        Assert.Null(part.Url);
        Assert.Equal("Подписка без URL", part.Text);
    }
}
