using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class OnboardingRestoreNavigationTests
{
    [Theory]
    [InlineData(OnboardingStep.Welcome, false, OnboardingStep.Welcome)]
    [InlineData(OnboardingStep.Welcome, true, OnboardingStep.Welcome)]
    [InlineData(OnboardingStep.Profile, false, OnboardingStep.Profile)]
    [InlineData(OnboardingStep.Profile, true, OnboardingStep.Profile)]
    [InlineData(OnboardingStep.Telegram, false, OnboardingStep.Telegram)]
    [InlineData(OnboardingStep.Telegram, true, OnboardingStep.FirstChat)]
    [InlineData(OnboardingStep.FirstChat, false, OnboardingStep.Telegram)]
    [InlineData(OnboardingStep.FirstChat, true, OnboardingStep.FirstChat)]
    [InlineData(OnboardingStep.Synchronization, false, OnboardingStep.Telegram)]
    [InlineData(OnboardingStep.Synchronization, true, OnboardingStep.Synchronization)]
    [InlineData(OnboardingStep.Workspace, false, OnboardingStep.Telegram)]
    [InlineData(OnboardingStep.Workspace, true, OnboardingStep.Synchronization)]
    public void RestoringSavedStep_RespectsTelegramAuthorization(
        OnboardingStep saved, bool authorized, OnboardingStep expected)
    {
        var settings = new AppSettings
        {
            UserName = "Test",
            CurrentOnboardingStep = saved
        };

        Assert.Equal(expected, OnboardingRestorePolicy.Resolve(settings, authorized).Step);
    }

    [Theory]
    [InlineData(OnboardingStep.Telegram)]
    [InlineData(OnboardingStep.FirstChat)]
    [InlineData(OnboardingStep.Synchronization)]
    public void MissingProfileName_CannotBeBypassedBySavedStep(OnboardingStep saved)
    {
        foreach (var authorized in new[] { false, true })
        {
            var settings = new AppSettings
            {
                UserName = "   ",
                CurrentOnboardingStep = saved
            };
            Assert.Equal(OnboardingStep.Profile, OnboardingRestorePolicy.Resolve(settings, authorized).Step);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedOnboarding_RestoresWorkspaceEvenIfSessionNeedsRecovery(bool authorized)
    {
        var settings = new AppSettings
        {
            IsOnboardingCompleted = true,
            CurrentOnboardingStep = OnboardingStep.Telegram
        };

        Assert.Equal(OnboardingStep.Workspace, OnboardingRestorePolicy.Resolve(settings, authorized).Step);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorruptStep_FallsBackToWelcome(bool authorized)
    {
        var settings = new AppSettings
        {
            UserName = "Test",
            CurrentOnboardingStep = (OnboardingStep)int.MaxValue
        };
        Assert.Equal(OnboardingStep.Welcome, OnboardingRestorePolicy.Resolve(settings, authorized).Step);
    }

    [Fact]
    public void Resolve_DoesNotMutatePersistedSettings()
    {
        var settings = new AppSettings
        {
            UserName = "Test",
            CurrentOnboardingStep = OnboardingStep.Synchronization,
            CurrentTelegramPage = TelegramOnboardingPage.ApiCredentials
        };

        Assert.Equal(OnboardingStep.Telegram, OnboardingRestorePolicy.Resolve(settings, false).Step);
        Assert.Equal(OnboardingStep.Synchronization, settings.CurrentOnboardingStep);
        Assert.Equal(TelegramOnboardingPage.ApiCredentials, settings.CurrentTelegramPage);
        Assert.False(settings.IsOnboardingCompleted);
    }

    [Theory]
    [InlineData(OnboardingStep.Telegram, true, true)]
    [InlineData(OnboardingStep.Telegram, false, false)]
    [InlineData(OnboardingStep.FirstChat, true, false)]
    [InlineData(OnboardingStep.FirstChat, false, true)]
    [InlineData(OnboardingStep.Synchronization, true, false)]
    [InlineData(OnboardingStep.Synchronization, false, true)]
    [InlineData(OnboardingStep.Workspace, true, false)]
    [InlineData(OnboardingStep.Workspace, false, true)]
    public void OnlyTelegramAuthorizationRedirect_IsPersisted(
        OnboardingStep saved, bool authorized, bool shouldPersist)
    {
        var settings = new AppSettings { UserName = "Test", CurrentOnboardingStep = saved };

        Assert.Equal(shouldPersist,
            OnboardingRestorePolicy.Resolve(settings, authorized).PersistRedirect);
    }

    [Fact]
    public void MissingNameRedirect_DoesNotSilentlyMarkStepAsSaved()
    {
        var settings = new AppSettings { CurrentOnboardingStep = OnboardingStep.Synchronization };
        var decision = OnboardingRestorePolicy.Resolve(settings, false);

        Assert.Equal(OnboardingStep.Profile, decision.Step);
        Assert.False(decision.PersistRedirect);
    }

    [Fact]
    public void Resolve_RejectsMissingSettings()
        => Assert.Throws<ArgumentNullException>(() => OnboardingRestorePolicy.Resolve(null!, true));
}
