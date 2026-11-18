using System.IO;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramSettingsTests
{
    [Theory]
    [InlineData("@My.Profile_7", "my.profile_7")]
    [InlineData("  MY_ACCOUNT  ", "my_account")]
    public void Normalize_ProducesCanonicalUserName(string input, string expected)
        => Assert.Equal(expected, InstagramUserName.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("instagram.com/example")]
    [InlineData("user name")]
    [InlineData("bad..name")]
    [InlineData(".bad")]
    public void Normalize_RejectsInvalidUserNames(string input)
        => Assert.ThrowsAny<ArgumentException>(() => InstagramUserName.Normalize(input));

    [Fact]
    public async Task SavesBothAccountsWithoutChangingOtherSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chatpulse-instagram-{Guid.NewGuid():N}");
        try
        {
            using var store = new JsonSettingsService(directory);
            await store.SaveUserNameAndStepAsync("Tester", OnboardingStep.Profile);
            await store.SaveInstagramAccountsAsync("@Main.Profile", "@Backup.Profile");
            var saved = await store.LoadAsync();
            Assert.Equal("main.profile", saved.InstagramTargetUserName);
            Assert.Equal("backup.profile", saved.InstagramBrowserUserName);
            Assert.Equal("Tester", saved.UserName);

            using var reopened = new JsonSettingsService(directory);
            var restored = await reopened.LoadAsync();
            Assert.Equal("main.profile", restored.InstagramTargetUserName);
            Assert.Equal("backup.profile", restored.InstagramBrowserUserName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidUserNameDoesNotOverwriteSavedAccounts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chatpulse-instagram-{Guid.NewGuid():N}");
        try
        {
            using var store = new JsonSettingsService(directory);
            await store.SaveInstagramAccountsAsync("main", "backup");
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => store.SaveInstagramAccountsAsync("newmain", "invalid/name"));
            var saved = await store.LoadAsync();
            Assert.Equal("main", saved.InstagramTargetUserName);
            Assert.Equal("backup", saved.InstagramBrowserUserName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
