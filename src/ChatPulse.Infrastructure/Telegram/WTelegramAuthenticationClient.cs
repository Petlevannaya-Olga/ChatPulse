using System.Security.Cryptography;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using WTelegram;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>
/// Управляет одной пользовательской MTProto-сессией. Сохранённая сессия дополнительно
/// защищена Windows DPAPI (CurrentUser). Пароли и коды никогда не записываются на диск.
/// </summary>
public sealed class WTelegramAuthenticationClient(string dataDirectory) : ITelegramAuthenticationClient, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Client? _client;
    private string? _awaitingInput;
    private bool _disposed;

    public async Task<TelegramLoginProgress> BeginLoginAsync(
        string phoneNumber, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_client is null)
            {
                _client = CreateClient();
            }

            // Вход здесь начинается с номера. При повторном входе с той же сессией
            // WTelegramClient возвращает авторизованного пользователя без нового кода.
            var next = await _client.Login(phoneNumber);
            return ToProgress(next);
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (Exception)
        {
            throw new TelegramAuthenticationException("telegram.login_failed",
                "Telegram не принял запрос. Проверьте номер, подключение и данные приложения.");
        }
        finally { _gate.Release(); }
    }

    public async Task<TelegramLoginProgress> ContinueLoginAsync(
        string input, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_client is null || _awaitingInput is null)
            {
                throw new TelegramAuthenticationException("telegram.login_not_started",
                    "Сначала укажите номер телефона и запросите код.");
            }

            var next = await _client.Login(input);
            return ToProgress(next);
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (Exception)
        {
            throw new TelegramAuthenticationException("telegram.verification_failed",
                "Не удалось подтвердить вход. Проверьте введённые данные и повторите попытку.");
        }
        finally { _gate.Release(); }
    }

    private Client CreateClient()
    {
        var idText = Environment.GetEnvironmentVariable("CHATPULSE_TELEGRAM_API_ID");
        var hash = Environment.GetEnvironmentVariable("CHATPULSE_TELEGRAM_API_HASH");
        if (!int.TryParse(idText, out var apiId) || apiId <= 0 ||
            hash is null || hash.Length != 32 || !hash.All(Uri.IsHexDigit))
        {
            throw new TelegramAuthenticationException("telegram.configuration_missing",
                "Перед первым входом разработчик должен настроить CHATPULSE_TELEGRAM_API_ID и CHATPULSE_TELEGRAM_API_HASH. См. TELEGRAM_SETUP.md.");
        }

        Directory.CreateDirectory(dataDirectory);
        var sessionPath = Path.Combine(dataDirectory, "telegram.session.dpapi");
        byte[] storedSession;
        try
        {
            storedSession = File.Exists(sessionPath)
                ? ProtectedData.Unprotect(File.ReadAllBytes(sessionPath), null, DataProtectionScope.CurrentUser)
                : [];
        }
        catch (CryptographicException)
        {
            throw new TelegramAuthenticationException("telegram.session_unavailable",
                "Не удалось прочитать локальную Telegram-сессию. Проверьте учётную запись Windows.");
        }

        // Созданная библиотекой сессия сериализуется через DPAPI. Кроме этого
        // WTelegramClient выполняет собственное шифрование сессии.
        return new Client(
            what => what switch
            {
                "api_id" => apiId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "api_hash" => hash,
                _ => null
            },
            storedSession,
            bytes => SaveSession(sessionPath, bytes));
    }

    private static void SaveSession(string path, byte[] rawSession)
    {
        var protectedBytes = ProtectedData.Protect(rawSession, null, DataProtectionScope.CurrentUser);
        var tmpPath = path + ".tmp";
        File.WriteAllBytes(tmpPath, protectedBytes);
        File.Move(tmpPath, path, true);
    }

    private TelegramLoginProgress ToProgress(string? next)
    {
        _awaitingInput = next;
        return next switch
        {
            null when _client?.User is { } user =>
                new TelegramLoginProgress(TelegramLoginInputKind.None,
                    string.Join(" ", new[] { user.first_name, user.last_name }
                        .Where(s => !string.IsNullOrWhiteSpace(s))), user.id),
            "verification_code" => new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode),
            "password" => new TelegramLoginProgress(TelegramLoginInputKind.Password),
            "email" => new TelegramLoginProgress(TelegramLoginInputKind.Email),
            "email_verification_code" => new TelegramLoginProgress(TelegramLoginInputKind.EmailVerificationCode),
            "name" => throw new TelegramAuthenticationException("telegram.account_missing",
                "Аккаунт ещё не зарегистрирован. Сначала создайте его в официальном Telegram."),
            _ => throw new TelegramAuthenticationException("telegram.unsupported_step",
                "Telegram запросил дополнительное подтверждение, пока не поддерживаемое ChatPulse.")
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
        _gate.Dispose();
    }
}
