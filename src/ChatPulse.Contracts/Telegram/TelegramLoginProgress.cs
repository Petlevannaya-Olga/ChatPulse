namespace ChatPulse.Contracts.Telegram;

public enum TelegramLoginInputKind
{
    None,
    VerificationCode,
    Password,
    Email,
    EmailVerificationCode
}

public sealed record TelegramLoginProgress(
    TelegramLoginInputKind RequiredInput,
    string? DisplayName = null,
    long? UserId = null)
{
    public bool IsAuthorized => RequiredInput == TelegramLoginInputKind.None && UserId.HasValue;
}
