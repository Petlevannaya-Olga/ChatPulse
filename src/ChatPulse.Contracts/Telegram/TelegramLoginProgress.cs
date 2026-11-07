namespace ChatPulse.Contracts.Telegram;

public enum TelegramLoginInputKind
{
    None,
    VerificationCode,
    Password,
    Email,
    EmailVerificationCode
}

/// <summary>Способ, который сервер Telegram указал для доставки кода входа.</summary>
public enum TelegramCodeDeliveryKind
{
    Unknown,
    TelegramApp,
    Sms,
    Email,
    Call,
    Other
}

public sealed record TelegramLoginProgress(
    TelegramLoginInputKind RequiredInput,
    string? DisplayName = null,
    long? UserId = null,
    TelegramCodeDeliveryKind CodeDelivery = TelegramCodeDeliveryKind.Unknown,
    bool HasResendOption = false,
    DateTimeOffset? ResendAvailableAtUtc = null)
{
    public bool IsAuthorized => RequiredInput == TelegramLoginInputKind.None && UserId.HasValue;
}
