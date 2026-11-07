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
/// Подэтап подключения Telegram. Не содержит API-ключей или кодов авторизации.
/// </summary>
public enum TelegramOnboardingPage
{
    ApiCredentials,
    Phone,
}

/// <summary>
/// Только пользовательские настройки; коды, пароли, api_hash и MTProto-сессия здесь не хранятся.
/// </summary>
public sealed class AppSettings
{
    public string? UserName { get; set; }

    public OnboardingStep CurrentOnboardingStep { get; set; } = OnboardingStep.Welcome;

    public TelegramOnboardingPage CurrentTelegramPage { get; set; } = TelegramOnboardingPage.ApiCredentials;

    public bool IsOnboardingCompleted { get; set; }
}
