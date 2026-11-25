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

    [Theory]
    [InlineData("Техническая ошибка: session not created: Chrome failed to start: crashed")]
    [InlineData("Ошибка Instagram: invalid session id")]
    [InlineData("Проверка не завершена: DevToolsActivePort file doesn't exist")]
    [InlineData("Ошибка чтения состояния Instagram: stale element reference")]
    [InlineData("Не удалось проверить комментарий: InvalidOperationException")]
    public void TechnicalResult_AbortsBulkInsteadOfAdvancing(string diagnostic)
    {
        var result = InstagramTaskCheckResult.Inconclusive(diagnostic);
        var exception = Assert.Throws<WorkspaceBulkTechnicalFailureException>(() =>
            InstagramBrowserFailure.ThrowIfTechnicalResult(result));
        Assert.Equal(diagnostic, exception.Message);
    }

    [Theory]
    [InlineData("Подписка не подтверждена")]
    [InlineData("Лайк не обнаружен после прокрутки")]
    [InlineData("Instagram не показал однозначный индикатор состояния")]
    public void OrdinaryUnconfirmedResult_DoesNotAbortBulk(string diagnostic)
    {
        InstagramBrowserFailure.ThrowIfTechnicalResult(InstagramTaskCheckResult.Inconclusive(diagnostic));
        InstagramBrowserFailure.ThrowIfTechnicalResult(InstagramTaskCheckResult.Confirmed("Лайк найден"));
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
