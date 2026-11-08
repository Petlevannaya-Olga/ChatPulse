namespace ChatPulse.Application.Features.Telegram.ApiCredentials;

/// <summary>
/// Changing the encrypted credentials succeeded, but the subsequent local session reset
/// failed. Restoring the previous credentials is attempted before this is thrown.
/// The in-memory Telegram client may already have been reset; the UI must not keep
/// displaying the previous authorization as verified.
/// </summary>
public sealed class TelegramApiCredentialsUpdateException(
    bool previousCredentialsRestored, Exception innerException)
    : Exception("Не удалось завершить изменение локальных Telegram API-ключей.", innerException)
{
    public bool PreviousCredentialsRestored { get; } = previousCredentialsRestored;
}
