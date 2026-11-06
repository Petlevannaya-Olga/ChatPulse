namespace ChatPulse.Application.Services;

public sealed class TelegramAuthenticationException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}
