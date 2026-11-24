using ChatPulse.Desktop.Services;
using OpenQA.Selenium;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramBrowserFailureTests
{
    [Theory]
    [InlineData("invalid session id")]
    [InlineData("chrome not reachable")]
    [InlineData("disconnected: not connected to DevTools")]
    public void FatalDriverFailure_AbortsInsteadOfReturningInconclusive(string message)
    {
        var error = new WebDriverException(message);
        Assert.True(InstagramBrowserFailure.IsFatal(error));
        var failure = Assert.Throws<InstagramBrowserSessionException>(() =>
            InstagramBrowserFailure.ThrowIfFatal(error, "Chrome"));
        Assert.Same(error, failure.InnerException);
        Assert.Contains(message, failure.Message);
    }

    [Fact]
    public void InstagramSessionFailure_HasExplicitType()
    {
        var failure = new InstagramBrowserSessionException("Сессия Instagram потеряна.");
        Assert.Contains("Сессия Instagram", failure.Message);
    }

    [Fact]
    public void TransientDomFailure_DoesNotAbortOtherTasks()
    {
        WebDriverException[] transient =
        [
            new StaleElementReferenceException("rerender"),
            new NoSuchElementException("not rendered"),
            new ElementClickInterceptedException("overlay")
        ];
        foreach (var exception in transient)
        {
            Assert.False(InstagramBrowserFailure.IsFatal(exception));
            InstagramBrowserFailure.ThrowIfFatal(exception, "DOM");
        }
    }
}
