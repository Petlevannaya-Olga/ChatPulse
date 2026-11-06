namespace ChatPulse.Desktop.Settings;

/// <summary>
/// Фактический этап мастера настройки. Сохраняется между запусками.
/// </summary>
public enum OnboardingStep
{
    Welcome,
    Profile,
    Telegram,
    FirstChat,
}

/// <summary>
/// Только пользовательские настройки; коды, пароли, api_hash и MTProto-сессия здесь не хранятся.
/// </summary>
public sealed class AppSettings
{
    public string? UserName { get; set; }

    public OnboardingStep CurrentOnboardingStep { get; set; } = OnboardingStep.Welcome;

    public bool IsOnboardingCompleted { get; set; }
}
