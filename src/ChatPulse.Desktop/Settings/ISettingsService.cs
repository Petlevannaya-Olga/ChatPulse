namespace ChatPulse.Desktop.Settings;

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveOnboardingStepAsync(
        OnboardingStep step, CancellationToken cancellationToken = default);

    Task SaveUserNameAndStepAsync(
        string userName, OnboardingStep step,
        CancellationToken cancellationToken = default);

    Task CompleteOnboardingAsync(CancellationToken cancellationToken = default);
}
