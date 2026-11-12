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
    string dataDirectory, ITelegramApiCredentialsStore credentialsStore,
    ITelegramCredentialChangeJournal? changeJournal = null,
    TelegramAuthorizationEvents? authorizationEvents = null)
    : ITelegramAuthenticationClient, IDisposable
{
    private static readonly TimeSpan RemoteRequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sessionFileLock = new();
    private readonly ITelegramCredentialChangeJournal _changeJournal =
        changeJournal ?? new FileTelegramCredentialChangeJournal(dataDirectory);
    private readonly TelegramAuthorizationEvents _authorizationEvents = authorizationEvents ?? new();
    private long _sessionGeneration;
    private Client? _client;
    private Func<TL.IObject, Task>? _notificationObserver;
    private string? _awaitingInput;
    private TelegramCodeDeliveryKind _codeDelivery = TelegramCodeDeliveryKind.Unknown;
    private DateTimeOffset? _resendAvailableAtUtc;
    private bool _hasResendOption;
    private int _disposed;
    private readonly TaskCompletionSource<bool> _disposeCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            // A crash during credential rotation must never reuse an old session.
            if (await RecoverInterruptedCredentialsUpdateAsync(cancellationToken))
                return new TelegramLoginProgress(TelegramLoginInputKind.None);
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
                user = await AwaitRemoteAsync(
                    _client.LoginUserIfNeeded(reloginOnFailedResume: false), cancellationToken);
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
        catch (TL.RpcException exception) when (IsAuthorizationLost(exception))
        {
            // Не удаляем защищённый файл без согласия пользователя.
            await DetachClientAsync();
            _authorizationEvents.ReportAuthorizationLost();
            Debug.WriteLine("[ChatPulse][TelegramAuth] RestoreSession: saved authorization rejected");
            throw new TelegramAuthenticationException("telegram.session_revoked",
                "Telegram отклонил сохранённую сессию. Очистите локальную сессию и войдите заново.");
        }
        catch (TelegramAuthenticationException exception) when (exception.Code is
                   "telegram.network_unavailable" or "telegram.request_timeout" or "telegram.session_restore_failed")
        {
            // Не используем повторно клиент, который пережил сетевую ошибку.
            // TelegramRemoteAwait уже отсоединяет клиента при таймауте/потере сети;
            // эта проверка также покрывает ошибки до входа в AwaitRemoteAsync.
            if (_client is not null) await DetachClientAsync();
            Debug.WriteLine($"[ChatPulse][TelegramAuth] RestoreSession: transient failure code={exception.Code}");
            throw;
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] RestoreSession: error type={exception.GetType().Name}");
            // Запрос мог завершиться до обёртки с таймаутом: такой клиент
            // нельзя безопасно переиспользовать при следующей проверке.
            await DetachClientAsync();
            throw new TelegramAuthenticationException("telegram.session_restore_failed",
                "Не удалось проверить сохранённую Telegram-сессию. Проверьте интернет-соединение и повторите проверку.");
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
            await RecoverInterruptedCredentialsUpdateAsync(cancellationToken);
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
            var next = await AwaitRemoteAsync(_client.Login(phoneNumber), cancellationToken);
            Debug.WriteLine($"[ChatPulse][TelegramAuth] BeginLogin: next={SafeLoginStep(next)}, delivery={_codeDelivery}");
            return await CompleteLoginStepAsync(next);
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TL.RpcException ex) when (IsAuthorizationLost(ex))
        {
            await RejectRevokedAuthorizationAsync();
            throw new TelegramAuthenticationException("telegram.session_revoked",
                "Telegram отклонил текущую сессию. Очистите локальную сессию и выполните вход заново.");
        }
        catch (TL.RpcException ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] BeginLogin: RPC error={SafeRpcError(ex.Message)}, code={ex.Code}");
            throw new TelegramAuthenticationException("telegram.login_rpc_error",
                TelegramRpcErrorMessages.ForBegin(ex.Message));
        }
        catch (InvalidDataException)
        {
            throw new TelegramAuthenticationException("telegram.configuration_invalid",
                "Не удалось прочитать API-ключи. Проверьте локальное хранилище ключей.");
        }
        catch (Exception ex) when (TelegramNetworkErrors.IsTransportFailure(ex))
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] BeginLogin: transport error type={ex.GetType().Name}");
            await DetachClientAsync();
            throw NetworkUnavailableException();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] BeginLogin: exception type={ex.GetType().Name}");
            throw new TelegramAuthenticationException("telegram.login_failed",
                "Не удалось выполнить запрос к Telegram. Проверьте интернет-соединение и повторите попытку. " +
                "Если код уже пришёл, не запрашивайте его повторно без необходимости.");
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
            await RecoverInterruptedCredentialsUpdateAsync(cancellationToken);
            if (_client is null || _awaitingInput is null)
            {
                throw new TelegramAuthenticationException("telegram.login_not_started",
                    "Сначала укажите номер телефона и запросите код.");
            }

            Debug.WriteLine($"[ChatPulse][TelegramAuth] ContinueLogin: submitting step={SafeLoginStep(_awaitingInput)}");
            var next = await AwaitRemoteAsync(_client.Login(input), cancellationToken);
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ContinueLogin: next={SafeLoginStep(next)}");
            return await CompleteLoginStepAsync(next);
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TL.RpcException ex) when (IsAuthorizationLost(ex))
        {
            await RejectRevokedAuthorizationAsync();
            throw new TelegramAuthenticationException("telegram.session_revoked",
                "Telegram отклонил текущую сессию. Очистите локальную сессию и выполните вход заново.");
        }
        catch (TL.RpcException ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ContinueLogin: RPC error={SafeRpcError(ex.Message)}, code={ex.Code}");
            // Показываем только заранее определённые сообщения. Не передаём
            // в интерфейс текст исключения, который может содержать детали запроса.
            throw new TelegramAuthenticationException("telegram.rpc_login_error",
                TelegramRpcErrorMessages.ForConfirmation(ex.Message, ex.Code));
        }
        catch (Exception ex) when (TelegramNetworkErrors.IsTransportFailure(ex))
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ContinueLogin: transport error type={ex.GetType().Name}");
            await DetachClientAsync();
            throw NetworkUnavailableException();
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
            await RecoverInterruptedCredentialsUpdateAsync(cancellationToken);
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
            var next = await AwaitRemoteAsync(_client.Login(string.Empty), cancellationToken);
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ResendCode: next={SafeLoginStep(next)}, delivery={_codeDelivery}");
            return await CompleteLoginStepAsync(next);
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TL.RpcException ex) when (IsAuthorizationLost(ex))
        {
            await RejectRevokedAuthorizationAsync();
            throw new TelegramAuthenticationException("telegram.session_revoked",
                "Telegram отклонил текущую сессию. Очистите локальную сессию и выполните вход заново.");
        }
        catch (TL.RpcException ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ResendCode: RPC error={SafeRpcError(ex.Message)}, code={ex.Code}");
            throw new TelegramAuthenticationException("telegram.resend_failed",
                TelegramRpcErrorMessages.ForResend(ex.Message, ex.Code));
        }
        catch (Exception ex) when (TelegramNetworkErrors.IsTransportFailure(ex))
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ResendCode: transport error type={ex.GetType().Name}");
            await DetachClientAsync();
            throw NetworkUnavailableException();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatPulse][TelegramAuth] ResendCode: exception type={ex.GetType().Name}");
            throw new TelegramAuthenticationException("telegram.resend_failed",
                "Не удалось повторно запросить код. Проверьте соединение с Telegram.");
        }
        finally { _gate.Release(); }
    }

    public async Task CancelLoginAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_awaitingInput is null)
                throw new TelegramAuthenticationException("telegram.login_not_pending",
                    "Нет незавершённого входа для отмены.");
            // Та же безопасная локальная очистка, что и при ручном сбросе.
            // Секреты API и настройки других аккаунтов не затрагиваем.
            ResetLocalSessionCore();
        }
        finally { _gate.Release(); }
    }

    public async Task ResetSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            ResetLocalSessionCore();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Invalidates callbacks from an old WTelegram.Client BEFORE disposing it.
    /// A late session-save callback cannot resurrect a session after reset.
    /// Must be called while holding _gate.
    /// </summary>
    private void ResetLocalSessionCore()
    {
        Client? oldClient;
        Func<TL.IObject, Task>? oldObserver;
        lock (_sessionFileLock)
        {
            _sessionGeneration++;
            oldClient = _client;
            oldObserver = _notificationObserver;
            _client = null;
            _notificationObserver = null;
            _awaitingInput = null;
            _codeDelivery = TelegramCodeDeliveryKind.Unknown;
            _hasResendOption = false;
            _resendAvailableAtUtc = null;
            _resumeUsingStoredUserId = false;
        }

        Exception? disposeFailure = null;
        try
        {
            if (oldClient is not null)
            {
                if (oldObserver is not null)
                    oldClient.OnOther -= oldObserver;
                oldClient.Dispose();
            }
        }
        catch (Exception ex) { disposeFailure = ex; }

        // A failing Dispose must not prevent deletion of the session file.
        Exception? deleteFailure = null;
        try
        {
            var session = Path.Combine(dataDirectory, "telegram.session.dpapi");
            lock (_sessionFileLock)
            {
                File.Delete(session);
                File.Delete(session + ".tmp");
            }
        }
        catch (Exception ex) { deleteFailure = ex; }

        if (disposeFailure is not null && deleteFailure is not null)
            throw new AggregateException(disposeFailure, deleteFailure);
        if (disposeFailure is not null) throw disposeFailure;
        if (deleteFailure is not null) throw deleteFailure;
    }

    /// <summary>
    /// Recovery is intentionally conservative: an interrupted credential update
    /// invalidates the session even if the old keys were later restored.
    /// </summary>
    private async Task<bool> RecoverInterruptedCredentialsUpdateAsync(CancellationToken cancellationToken)
    {
        if (!await _changeJournal.IsPendingAsync(cancellationToken)) return false;

        // If deletion fails, keep the marker: a later attempt must retry.
        ResetLocalSessionCore();
        await _changeJournal.ClearPendingAsync(CancellationToken.None);
        Debug.WriteLine("[ChatPulse][TelegramAuth] Interrupted credential update recovered");
        return true;
    }

    /// <summary>Читает список доступных групп и каналов без загрузки истории сообщений.</summary>
    public async Task<TelegramChatsSnapshot> GetChatsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await RecoverInterruptedCredentialsUpdateAsync(cancellationToken);
            var telegramUserId = GetVerifiedUserId();

            var dialogs = await AwaitRemoteAsync(_client!.Messages_GetAllDialogs(), cancellationToken);

            // ID в Telegram не глобально уникален между Chat и Channel:
            // сохраняем также тип peer, а не только числовой идентификатор.
            var chats = dialogs.dialogs
                .Select(dialog => dialogs.UserOrChat(dialog))
                .OfType<TL.ChatBase>()
                .Where(chat => chat.IsActive)
                .Select(chat => new TelegramChatOption(
                    chat.ID,
                    chat is TL.Chat ? "Chat" : "Channel",
                    chat switch
                    {
                        TL.Chat group => group.title,
                        TL.Channel channel => channel.title,
                        _ => chat.ToString() ?? "Чат Telegram"
                    },
                    chat.IsChannel ? "Канал" : "Группа"))
                .DistinctBy(chat => (chat.PeerType, chat.PeerId))
                .OrderBy(chat => chat.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            return new TelegramChatsSnapshot(telegramUserId, chats);
        }
        catch (TelegramAuthenticationException exception) when (exception.Code == "telegram.request_timeout")
        {
            // После таймаута старый клиент отсоединён. До новой проверки не утверждаем,
            // что ранее подтверждённый пользователь по-прежнему подключён.
            _authorizationEvents.ReportVerificationRequired();
            throw;
        }
        catch (TelegramAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation of an in-flight RPC detaches the WTelegram client.
            // Do not keep the old account badge verified until session restore.
            if (_client is null)
                _authorizationEvents.ReportVerificationRequired();
            throw;
        }
        catch (TL.RpcException exception) when (IsAuthorizationLost(exception))
        {
            await DetachClientAsync();
            _authorizationEvents.ReportAuthorizationLost();
            throw new TelegramAuthenticationException("telegram.session_revoked",
                "Telegram завершил эту авторизацию. Очистите локальную сессию и войдите снова.");
        }
        catch (TL.RpcException exception)
        {
            Debug.WriteLine($"[ChatPulse][TelegramChats] GetChats: rpcCode={exception.Code}");
            throw new TelegramAuthenticationException("telegram.chats_load_failed",
                "Telegram не смог загрузить список чатов. Проверьте соединение и повторите попытку.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[ChatPulse][TelegramChats] GetChats: exceptionType={exception.GetType().Name}");
            throw new TelegramAuthenticationException("telegram.chats_load_failed",
                "Не удалось получить список Telegram-чатов. Повторите попытку.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Нельзя использовать UserId из неподтверждённого файла сессии.</summary>
    public async Task<long> GetAuthorizedUserIdAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await RecoverInterruptedCredentialsUpdateAsync(cancellationToken);
            return GetVerifiedUserId();
        }
        finally { _gate.Release(); }
    }

    private long GetVerifiedUserId()
    {
        var user = _client?.User;
        if (user is null || user.id <= 0)
        {
            throw new TelegramAuthenticationException("telegram.not_authorized",
                "Telegram не подключён. Вернитесь к шагу авторизации.");
        }
        return user.id;
    }

    private static bool IsAuthorizationLost(TL.RpcException exception)
        => TelegramAuthorizationFailure.IsLost(exception.Code, exception.Message);

    // Shared recovery for RPC failures during login, confirmation, and resend.
    // The protected session file remains until the user confirms local cleanup.
    private async Task RejectRevokedAuthorizationAsync()
    {
        await DetachClientAsync();
        _authorizationEvents.ReportAuthorizationLost();
    }

    // Библиотека не принимает CancellationToken для этих вызовов напрямую.
    // После прерывания старый Client изолируется, но запрос на сервере может завершиться позже.
    private async Task<T> AwaitRemoteAsync<T>(Task<T> request, CancellationToken cancellationToken)
    {
        // A local network disconnect should fail promptly instead of waiting
        // for the full remote timeout. An upstream outage still uses the timeout.
        using var network = new TelegramNetworkAvailabilityMonitor();
        return await TelegramRemoteAwait.AwaitAsync(request, RemoteRequestTimeout,
            cancellationToken, DetachClientAsync, network.UnavailableToken);
    }

    private static TelegramAuthenticationException NetworkUnavailableException() =>
        new("telegram.network_unavailable",
            "Не удалось связаться с Telegram. Проверьте интернет-соединение и повторите попытку. " +
            "Если код уже пришёл, не запрашивайте его повторно без необходимости.");

    /// <summary>Отсоединяет устаревший клиент, не удаляя локальную сессию.</summary>
    private async Task DetachClientAsync()
    {
        Client? stale;
        Func<TL.IObject, Task>? oldObserver;
        lock (_sessionFileLock)
        {
            _sessionGeneration++;
            stale = _client;
            oldObserver = _notificationObserver;
            _client = null;
            _notificationObserver = null;
            _awaitingInput = null;
            _codeDelivery = TelegramCodeDeliveryKind.Unknown;
            _hasResendOption = false;
            _resendAvailableAtUtc = null;
            _resumeUsingStoredUserId = false;
        }

        if (stale is null) return;
        if (oldObserver is not null)
            stale.OnOther -= oldObserver;
        try
        {
            // Даже если DisposeAsync зависнет, старый callback уже изолирован generation.
            await stale.DisposeAsync().AsTask().WaitAsync(DisposeTimeout);
        }
        catch (Exception exception)
        {
            // После detach устаревший клиент уже не может вернуть состояние авторизации.
            // Ошибка Dispose не должна маскировать исходную ошибку RPC или таймаут.
            Debug.WriteLine($"[ChatPulse][TelegramAuth] Dispose of stale client: {exception.GetType().Name}");
        }
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
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new TelegramAuthenticationException("telegram.session_unavailable",
                "Локальную Telegram-сессию не удалось прочитать. Можно очистить её и войти заново; API-ключи сохранятся.");
        }

        // Созданная библиотекой сессия сериализуется через DPAPI. Кроме этого
        // WTelegramClient выполняет собственное шифрование сессии.
        Debug.WriteLine($"[ChatPulse][TelegramAuth] CreateClient: sessionExists={File.Exists(sessionPath)}");
        var generation = Volatile.Read(ref _sessionGeneration);
        try
        {
            var client = new Client(
                what => what switch
                {
                    "api_id" => credentials.ApiId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "api_hash" => credentials.ApiHash,
                    "user_id" => _resumeUsingStoredUserId ? "-1" : null,
                    _ => null
                },
                storedSession,
                bytes => SaveSession(sessionPath, bytes, generation));
            Func<TL.IObject, Task> observer = notification => ObserveLoginNotificationAsync(notification, generation);
            client.OnOther += observer;
            _notificationObserver = observer;
            return client;
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or CryptographicException or EndOfStreamException or ArgumentException)
        {
            throw new TelegramAuthenticationException("telegram.session_corrupted",
                "Сохранённая Telegram-сессия повреждена или несовместима. Очистите локальную сессию и войдите заново.");
        }
    }

    /// <summary>
    /// WTelegram передаёт auth.sentCode через OnOther. Это выбранный сервером
    /// канал, а не доказательство фактической доставки сообщения.
    /// Не сохраняем и не логируем номер, код или phone_code_hash.
    /// </summary>
    private Task ObserveLoginNotificationAsync(TL.IObject notification, long generation)
    {
        lock (_sessionFileLock)
        {
            // Позднее уведомление отменённого запроса не меняет состояние нового входа.
            if (generation != _sessionGeneration) return Task.CompletedTask;

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

    private void SaveSession(string path, byte[] rawSession, long generation)
    {
        lock (_sessionFileLock)
        {
            if (generation != _sessionGeneration) return;

            var protectedBytes = ProtectedData.Protect(rawSession, null, DataProtectionScope.CurrentUser);
            try
            {
                var tmpPath = path + ".tmp";
                try
                {
                    File.WriteAllBytes(tmpPath, protectedBytes);
                    File.Move(tmpPath, path, true);
                }
                finally
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
            }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
    }

    /// <summary>
    /// WTelegram.Login keeps the last requested field internally. If Telegram
    /// requests sign-up (name) or an unsupported step, a later Login(phone)
    /// would be interpreted as the answer to that field. Discard the unfinished
    /// login and its local session before allowing another attempt.
    /// Called while holding the client gate.
    /// </summary>
    internal async Task<TelegramLoginProgress> CompleteLoginStepAsync(string? next)
    {
        try
        {
            return ToProgress(next);
        }
        catch (TelegramAuthenticationException exception)
            when (exception.Code is "telegram.account_missing" or "telegram.unsupported_step")
        {
            try
            {
                // A durable marker also covers a crash between session deletion
                // and the completion of the local cleanup.
                await _changeJournal.MarkPendingAsync(CancellationToken.None);
                ResetLocalSessionCore();
                await _changeJournal.ClearPendingAsync(CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                Debug.WriteLine($"[ChatPulse][TelegramAuth] Unsupported login cleanup: {cleanupException.GetType().Name}");
                throw new TelegramAuthenticationException("telegram.session_cleanup_failed",
                    "Не удалось очистить незавершённую авторизацию. Очистите локальную сессию и повторите вход.");
            }

            throw;
        }
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    // A testable completion signal; shutdown does not block the WPF UI thread.
    internal Task DisposeCompletion => _disposeCompleted.Task;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ = DisposeClientWhenIdleAsync();
    }

    private async Task DisposeClientWhenIdleAsync()
    {
        // An active login or request owns _gate. Wait asynchronously to avoid
        // disposing WTelegram.Client underneath that operation or blocking OnExit.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Client? stale;
            Func<TL.IObject, Task>? observer;
            lock (_sessionFileLock)
            {
                _sessionGeneration++;
                stale = _client;
                observer = _notificationObserver;
                _client = null;
                _notificationObserver = null;
                _awaitingInput = null;
                _hasResendOption = false;
                _resendAvailableAtUtc = null;
                _codeDelivery = TelegramCodeDeliveryKind.Unknown;
                _resumeUsingStoredUserId = false;
            }

            if (stale is not null)
            {
                if (observer is not null)
                    stale.OnOther -= observer;
                stale.Dispose();
            }
        }
        catch (Exception exception)
        {
            // A shutdown failure should not crash the UI thread.
            Debug.WriteLine($"[ChatPulse][TelegramAuth] Shutdown: {exception.GetType().Name}");
        }
        finally
        {
            // Do not Dispose the semaphore here: already queued callers must
            // acquire it, see the disposed state, and safely release it.
            _gate.Release();
            _disposeCompleted.TrySetResult(true);
        }
    }
}
