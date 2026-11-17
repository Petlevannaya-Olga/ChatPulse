namespace ChatPulse.Desktop.Settings;

/// <summary>
/// Restores the onboarding screen without WPF or Telegram RPC side effects.
/// PersistRedirect is true only for a Telegram-authorization redirect, matching
/// the existing startup behavior (including legacy Workspace without completion).
/// </summary>
public readonly record struct OnboardingRestoreDecision(OnboardingStep Step, bool PersistRedirect);

public static class OnboardingRestorePolicy
{
    public static OnboardingRestoreDecision Resolve(AppSettings settings, bool isTelegramAuthorized)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.IsOnboardingCompleted)
            return new OnboardingRestoreDecision(OnboardingStep.Workspace, false);

        var step = settings.CurrentOnboardingStep;
        if (!Enum.IsDefined(step))
            return new OnboardingRestoreDecision(OnboardingStep.Welcome, false);
        if (step == OnboardingStep.Workspace)
            step = OnboardingStep.Synchronization;

        // A saved step cannot bypass the required profile name.
        if ((step is OnboardingStep.Telegram or OnboardingStep.FirstChat or OnboardingStep.Synchronization) &&
            string.IsNullOrWhiteSpace(settings.UserName))
            return new OnboardingRestoreDecision(OnboardingStep.Profile, false);

        if (isTelegramAuthorized && step == OnboardingStep.Telegram)
            return new OnboardingRestoreDecision(OnboardingStep.FirstChat, true);
        if (!isTelegramAuthorized &&
            (step is OnboardingStep.FirstChat or OnboardingStep.Synchronization))
            return new OnboardingRestoreDecision(OnboardingStep.Telegram, true);

        return new OnboardingRestoreDecision(step, false);
    }
}
