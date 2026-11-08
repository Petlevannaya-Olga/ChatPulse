namespace ChatPulse.Application.Services;

/// <summary>
/// Уведомление о потере подтверждённой авторизации во время запроса Telegram.
/// Не хранит и не публикует секреты либо личные данные.
/// </summary>
public sealed class TelegramAuthorizationEvents
{
    public event Action? AuthorizationLost;
    public event Action? VerificationRequired;

    public void ReportAuthorizationLost() => AuthorizationLost?.Invoke();
    public void ReportVerificationRequired() => VerificationRequired?.Invoke();
}
