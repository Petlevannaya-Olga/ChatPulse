namespace ChatPulse.Infrastructure.Telegram;

/// <summary>
/// Translates known Telegram RPC error identifiers into safe, actionable messages.
/// Never forwards arbitrary server error strings to the UI.
/// </summary>
internal static class TelegramRpcErrorMessages
{
    public static string ForBegin(string? code) => code switch
    {
        "API_ID_INVALID" => "Telegram отклонил API ID или API Hash. Проверьте ключи.",
        "PHONE_NUMBER_INVALID" => "Telegram отклонил номер телефона. Проверьте его формат.",
        "PHONE_NUMBER_BANNED" => "Telegram ограничил этот номер. Для разблокировки обратитесь в поддержку Telegram.",
        "PHONE_NUMBER_FLOOD" => "Telegram ограничил частоту запросов для номера. Повторите позже.",
        var value when IsFloodWait(value) =>
            "Telegram временно ограничил попытки входа. Дождитесь снятия ограничения.",
        _ => "Telegram отклонил запрос кода. Подробности доступны в Output Visual Studio без персональных данных."
    };

    public static string ForConfirmation(string? code, int rpcCode) => code switch
    {
        "PHONE_CODE_INVALID" => "Неверный код Telegram. Проверьте код из последнего сообщения.",
        "PHONE_CODE_EXPIRED" => "Срок действия кода Telegram истёк. Понадобится новый код.",
        "PHONE_CODE_EMPTY" => "Код подтверждения пустой.",
        "PHONE_CODE_HASH_INVALID" => "Telegram больше не принимает этот запрос подтверждения. Потребуется начать вход заново.",
        "PASSWORD_HASH_INVALID" => "Неверный пароль двухэтапной проверки Telegram.",
        "CODE_INVALID" or "EMAIL_TOKEN_INVALID" => "Неверный код подтверждения электронной почты.",
        "API_ID_INVALID" => "Telegram отклонил API ID или API Hash.",
        var value when IsFloodWait(value) =>
            "Telegram временно ограничил запросы. Не повторяйте вход до снятия ограничения.",
        _ => $"Telegram отклонил подтверждение входа (код ошибки {rpcCode})."
    };

    public static string ForResend(string? code, int rpcCode) => code switch
    {
        "SEND_CODE_UNAVAILABLE" => "Telegram не разрешает ещё одну отправку для этого номера.",
        "PHONE_CODE_EXPIRED" or "PHONE_CODE_HASH_INVALID" =>
            "Срок действия запроса истёк. Нужно начать вход заново.",
        "PHONE_NUMBER_FLOOD" => "Telegram ограничил частоту запросов кода. Повторите позже.",
        var value when IsFloodWait(value) =>
            "Telegram временно ограничил запросы кода. Дождитесь снятия ограничения.",
        _ => $"Не удалось повторно запросить код (ошибка Telegram {rpcCode})."
    };

    private static bool IsFloodWait(string? code)
        => code?.StartsWith("FLOOD_WAIT_", StringComparison.Ordinal) == true;
}
