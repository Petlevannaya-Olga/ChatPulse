using System.Diagnostics;
using System.Security.Cryptography;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using WTelegram;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>
/// Управляет одной пользовательской MTProto-сессией. Сохранённая сессия дополнительно
/// защищена Windows DPAPI (CurrentUser). Пароли и коды никогда не записываются на диск.
/// </summary>
public sealed class WTelegramAuthenticationClient(
    string dataDirectory, ITelegramApiCredentialsStore credentialsStore)
    : ITelegramAuthenticationClient, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Client? _client;
    private string? _awaitingInput;
    private TelegramCodeDeliveryKind _codeDelivery = TelegramCodeDeliveryKind.Unknown;
    private DateTimeOffset? _resendAvailableAtUtc;
    private bool _hasResendOption;
    private bool _disposed;
    private bool _resumeUsingStoredUserId;

    /// <summary>
    /// Проверяет сохранённую пользовательскую сессию, не начиная новый вход
    /// и не вызывая Auth_SendCode. Наличие файла само по себе не означает вход.
    /// </summary>
    public async Task<TelegramLoginProgress> RestoreSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var sessionPath = Path.Combine(dataDirectory, "telegram.session.dpapi");
            if (!File.Exists(sessionPath))
                return new TelegramLoginProgress(TelegramLoginInputKind.None);

            _client ??= await CreateClientAsync(cancellationToken);
            if (_client.UserId == 0)
            {
                Debug.WriteLine("[ChatPulse][TelegramAuth] RestoreSession: saved session is not authorized");
                return new TelegramLoginProgress(TelegramLoginInputKind.None);
            }

            // Без проверки телефона: он не сохраняется в settings.json.
            // user_id=-1 разрешён WTelegram только на время восстановления.
            // reloginOnFailedResume:false запрещает автоматический новый вход.
            TL.User user;
            _resumeUsingStoredUserId = true;
            try
            {
                user = await _client.LoginUserIfNeeded(reloginOnFailedResume: false);
            }
            finally
            {
                _resumeUsingStoredUserId = false;
            }

            _awaitingInput = null;
            _codeDelivery = TelegramCodeDeliveryKind.Unknown;
            Debug.WriteLine("[ChatPulse][TelegramAuth] RestoreSession: authorized session verified");
            return AuthorizedProgress(user);
        }
        catch (TL.RpcException exception) when (exception.Code == 401)
        {
            // Сессия могла быть отозвана на стороне Telegram. Не удаляем её молча.
            Debug.WriteLine("[ChatPulse][TelegramAuth] RestoreSession: Telegram rejected saved authorization");
            return new TelegramLoginProgress(TelegramLoginInputKind.None);
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] RestoreSession: error type={exception.GetType().Name}");
            throw new TelegramAuthenticationException("telegram.session_restore_failed",
                "Не удалось проверить сохранённую Telegram-сессию. Проверьте подключение к Telegram и повторите запуск.");
        }
        finally { _gate.Release(); }
    }

    public async Task<TelegramLoginProgress> BeginLoginAsync(
        string phoneNumber, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_client is null)
            {
                _client = await CreateClientAsync(cancellationToken);
            }

            _codeDelivery = TelegramCodeDeliveryKind.Unknown;
            _hasResendOption = false;
            _resendAvailableAtUtc = null;

            // В журнал отладки не попадают телефон, API-ключи, коды или данные сессии.
            Debug.WriteLine("[ChatPulse][TelegramAuth] BeginLogin: request started");

            // Вход здесь начинается с номера. При повторном входе с той же сессией
            // WTelegramClient возвращает авторизованного пользователя без нового кода.
            var next = await _client.Login(phoneNumber);
            Debug.WriteLine($"[ChatPulse][TelegramAuth] BeginLogin: next={SafeLoginStep(next)}, delivery={_codeDelivery}");
            return ToProgress(next);
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (TL.RpcException ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] BeginLogin: RPC error={SafeRpcError(ex.Message)}, code={ex.Code}");
            throw new TelegramAuthenticationException("telegram.login_rpc_error",
                "Telegram отклонил запрос кода. Причина записана в окно Output отладчика Visual Studio без персональных данных.");
        }
        catch (InvalidDataException)
        {
            throw new TelegramAuthenticationException("telegram.configuration_invalid",
                "Не удалось прочитать API-ключи. Проверьте локальное хранилище ключей.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] BeginLogin: exception type={ex.GetType().Name}");
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

            Debug.WriteLine($"[ChatPulse][TelegramAuth] ContinueLogin: submitting step={SafeLoginStep(_awaitingInput)}");
            var next = await _client.Login(input);
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ContinueLogin: next={SafeLoginStep(next)}");
            return ToProgress(next);
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TL.RpcException ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ContinueLogin: RPC error={SafeRpcError(ex.Message)}, code={ex.Code}");
            // Показываем только заранее определённые сообщения. Не передаём
            // в интерфейс текст исключения, который может содержать детали запроса.
            throw new TelegramAuthenticationException("telegram.rpc_login_error", ex.Message switch
            {
                "PHONE_CODE_INVALID" => "Неверный код Telegram. Проверьте код из последнего сообщения.",
                "PHONE_CODE_EXPIRED" => "Срок действия кода Telegram истёк. Понадобится новый код.",
                "PHONE_CODE_EMPTY" => "Код подтверждения пустой.",
                "PHONE_CODE_HASH_INVALID" => "Telegram больше не принимает этот запрос подтверждения. Потребуется начать вход заново.",
                "PASSWORD_HASH_INVALID" => "Неверный пароль двухэтапной проверки Telegram.",
                "CODE_INVALID" or "EMAIL_TOKEN_INVALID" => "Неверный код подтверждения электронной почты.",
                "API_ID_INVALID" => "Telegram отклонил API ID или API Hash.",
                var message when message.StartsWith("FLOOD_WAIT_", StringComparison.Ordinal) =>
                    "Telegram временно ограничил запросы. Не повторяйте вход до снятия ограничения.",
                _ => $"Telegram отклонил подтверждение входа (код ошибки {ex.Code})."
            });
        }
        catch (IOException)
        {
            throw new TelegramAuthenticationException("telegram.session_save_failed",
                "Ошибка записи Telegram-сессии. Проверьте доступ к папке ChatPulse в AppData.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new TelegramAuthenticationException("telegram.session_access_denied",
                "Нет доступа к сохранению Telegram-сессии. Проверьте права на папку ChatPulse в AppData.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ContinueLogin: exception type={ex.GetType().Name}");
            throw new TelegramAuthenticationException("telegram.verification_failed",
                "Не удалось подтвердить вход. Проверьте подключение к Telegram и повторите попытку.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// WTelegramClient 4.x: пустой verification_code означает запрос альтернативной доставки.
    /// Библиотека сама вызывает auth.resendCode с сохранённым phone_code_hash;
    /// раньше серверного timeout (или без next_type) ничего не отправляем.
    /// </summary>
    public async Task<TelegramLoginProgress> ResendCodeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_client is null || _awaitingInput != "verification_code")
            {
                throw new TelegramAuthenticationException("telegram.code_not_pending",
                    "Сейчас Telegram не ожидает код подтверждения.");
            }
            if (!_hasResendOption)
            {
                throw new TelegramAuthenticationException("telegram.resend_unavailable",
                    "Telegram не предложил повторную отправку другим способом для этого запроса.");
            }
            if (_resendAvailableAtUtc is { } availableAt && DateTimeOffset.UtcNow < availableAt)
            {
                throw new TelegramAuthenticationException("telegram.resend_too_early",
                    "Telegram ещё не разрешил повторную отправку. Дождитесь окончания обратного отсчёта.");
            }

            Debug.WriteLine("[ChatPulse][TelegramAuth] ResendCode: requested after server timeout");
            var next = await _client.Login(string.Empty);
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ResendCode: next={SafeLoginStep(next)}, delivery={_codeDelivery}");
            return ToProgress(next);
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TL.RpcException ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ResendCode: RPC error={SafeRpcError(ex.Message)}, code={ex.Code}");
            throw new TelegramAuthenticationException("telegram.resend_failed", ex.Message switch
            {
                "SEND_CODE_UNAVAILABLE" => "Telegram не разрешает ещё одну отправку для этого номера.",
                "PHONE_CODE_EXPIRED" or "PHONE_CODE_HASH_INVALID" =>
                    "Срок действия запроса истёк. Нужно начать вход заново.",
                "PHONE_NUMBER_FLOOD" =>
                    "Telegram ограничил частоту запросов кода. Повторите позже.",
                var message when message.StartsWith("FLOOD_WAIT_", StringComparison.Ordinal) =>
                    "Telegram временно ограничил запросы кода. Дождитесь снятия ограничения.",
                _ => $"Не удалось повторно запросить код (ошибка Telegram {ex.Code})."
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ResendCode: exception type={ex.GetType().Name}");
            throw new TelegramAuthenticationException("telegram.resend_failed",
                "Не удалось повторно запросить код. Проверьте соединение с Telegram.");
        }
        finally { _gate.Release(); }
    }

    public async Task ResetSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _client?.Dispose();
            _client = null;
            _awaitingInput = null;
            _codeDelivery = TelegramCodeDeliveryKind.Unknown;
            _hasResendOption = false;
            _resendAvailableAtUtc = null;
            var session = Path.Combine(dataDirectory, "telegram.session.dpapi");
            File.Delete(session);
            // Remove leftover temporary session writes from an interrupted save as well.
            File.Delete(session + ".tmp");
        }
        finally { _gate.Release(); }
    }

    private async Task<Client> CreateClientAsync(CancellationToken cancellationToken)
    {
        var credentials = await credentialsStore.LoadAsync(cancellationToken)
            ?? throw new TelegramAuthenticationException("telegram.configuration_missing",
                "Сначала сохраните API ID и API Hash на экране подключения Telegram.");

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
        Debug.WriteLine($"[ChatPulse][TelegramAuth] CreateClient: sessionExists={File.Exists(sessionPath)}");
        var client = new Client(
            what => what switch
            {
                "api_id" => credentials.ApiId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "api_hash" => credentials.ApiHash,
                "user_id" => _resumeUsingStoredUserId ? "-1" : null,
                _ => null
            },
            storedSession,
            bytes => SaveSession(sessionPath, bytes));
        client.OnOther += ObserveLoginNotificationAsync;
        return client;
    }

    /// <summary>
    /// WTelegram передаёт auth.sentCode через OnOther. Это выбранный сервером
    /// канал, а не доказательство фактической доставки сообщения.
    /// Не сохраняем и не логируем номер, код или phone_code_hash.
    /// </summary>
    private Task ObserveLoginNotificationAsync(TL.IObject notification)
    {
        if (notification is TL.Auth_SentCode sentCode)
        {
            // Только типы ответа и серверное время ожидания — ни кода, ни phone_code_hash.
            Debug.WriteLine($"[ChatPulse][TelegramAuth] Auth_SentCode: type={sentCode.type.GetType().Name}, nextType={sentCode.next_type}, timeoutSec={sentCode.timeout}");
            _hasResendOption = sentCode.next_type != 0;
            _resendAvailableAtUtc = _hasResendOption
                ? DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, sentCode.timeout))
                : null;
            _codeDelivery = sentCode.type.GetType().Name switch
            {
                "Auth_SentCodeTypeApp" => TelegramCodeDeliveryKind.TelegramApp,
                "Auth_SentCodeTypeSms" or "Auth_SentCodeTypeFirebaseSms" => TelegramCodeDeliveryKind.Sms,
                "Auth_SentCodeTypeEmailCode" => TelegramCodeDeliveryKind.Email,
                "Auth_SentCodeTypeCall" or "Auth_SentCodeTypeFlashCall" or "Auth_SentCodeTypeMissedCall" => TelegramCodeDeliveryKind.Call,
                _ => TelegramCodeDeliveryKind.Other
            };
        }
        else if (notification is TL.Auth_SentCodeBase)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] Auth_SentCodeBase: type={notification.GetType().Name}");
        }
        return Task.CompletedTask;
    }

    private static string SafeLoginStep(string? step) => step switch
    {
        null => "completed",
        "verification_code" => "verification_code",
        "password" => "password",
        "email" => "email",
        "email_verification_code" => "email_verification_code",
        "name" => "name",
        _ => "unexpected_step"
    };

    // Никогда не записываем произвольный текст серверного исключения:
    // в диагностику пропускаются только идентификаторы RPC вида PHONE_CODE_INVALID.
    private static string SafeRpcError(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 80 || value[0] < 'A' || value[0] > 'Z')
        {
            return "redacted";
        }

        foreach (var character in value)
        {
            if ((character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9') ||
                character == '_')
            {
                continue;
            }

            return "redacted";
        }

        return value;
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
                AuthorizedProgress(user),
            "verification_code" => new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode,
                CodeDelivery: _codeDelivery,
                HasResendOption: _hasResendOption,
                ResendAvailableAtUtc: _resendAvailableAtUtc),
            "password" => new TelegramLoginProgress(TelegramLoginInputKind.Password),
            "email" => new TelegramLoginProgress(TelegramLoginInputKind.Email),
            "email_verification_code" => new TelegramLoginProgress(TelegramLoginInputKind.EmailVerificationCode),
            "name" => throw new TelegramAuthenticationException("telegram.account_missing",
                "Аккаунт ещё не зарегистрирован. Сначала создайте его в официальном Telegram."),
            _ => throw new TelegramAuthenticationException("telegram.unsupported_step",
                "Telegram запросил дополнительное подтверждение, пока не поддерживаемое ChatPulse.")
        };
    }

    private static TelegramLoginProgress AuthorizedProgress(TL.User user)
        => new(TelegramLoginInputKind.None,
            string.Join(" ", new[] { user.first_name, user.last_name }
                .Where(name => !string.IsNullOrWhiteSpace(name))), user.id);

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
