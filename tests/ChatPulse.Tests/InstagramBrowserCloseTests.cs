using System.IO;
using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramBrowserCloseTests
{
    [Fact]
    public async Task CloseBrowserAsync_WithoutOpenBrowser_IsRepeatableAndPreservesProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChatPulse.BrowserCloseTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "keep-profile.txt");
        try
        {
            await File.WriteAllTextAsync(marker, "persisted-login-data");
            using (var browser = new InstagramBrowserService(directory))
            {
                // No ChromeDriver should be started to perform cleanup.
                await browser.CloseBrowserAsync();
                await browser.CloseBrowserAsync();
            }
            Assert.Equal("persisted-login-data", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
