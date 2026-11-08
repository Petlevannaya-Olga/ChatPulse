namespace ChatPulse.Infrastructure.Telegram;

/// <summary>Классификация ответа Telegram без передачи текста ошибки в UI.</summary>
internal static class TelegramAuthorizationFailure
{
    internal static bool IsLost(int rpcCode, string? rpcMessage)
        => rpcCode == 401 || rpcMessage is
            "AUTH_KEY_UNREGISTERED" or "SESSION_REVOKED" or "SESSION_EXPIRED" or
            "AUTH_KEY_INVALID" or "AUTH_KEY_DUPLICATED";
}
