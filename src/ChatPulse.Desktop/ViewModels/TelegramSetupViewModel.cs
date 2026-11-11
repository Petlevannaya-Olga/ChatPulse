using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ChatPulse.Application.Features.Telegram.ApiCredentials;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Delete;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Get;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Save;
using ChatPulse.Application.Features.Telegram.BeginLogin;
using ChatPulse.Application.Features.Telegram.CancelLogin;
using ChatPulse.Application.Services;
using ChatPulse.Application.Features.Telegram.ContinueLogin;
using ChatPulse.Application.Features.Telegram.RestoreSession;
using ChatPulse.Application.Features.Telegram.ResetSession;
using ChatPulse.Application.Features.Telegram.ResendCode;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Shared.Abstractions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ChatPulse.Desktop.ViewModels;

public sealed class TelegramSetupViewModel : INotifyPropertyChanged
{
    private readonly IServiceScopeFactory scopeFactory;

    public TelegramSetupViewModel(IServiceScopeFactory scopeFactory, TelegramAuthorizationEvents authorizationEvents)
    {
        this.scopeFactory = scopeFactory;
        authorizationEvents.AuthorizationLost += HandleAuthorizationLost;
        authorizationEvents.VerificationRequired += HandleVerificationRequired;
    }

    private void HandleVerificationRequired()
    {
        void Invalidate()
        {
            SetProgress(null);
            SetSessionRecoveryNeeded(false);
            SetSessionRetryNeeded(true);
            Error = "Не удалось подтвердить текущую Telegram-сессию. Повторите проверку после восстановления соединения.";
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke((Action)Invalidate);
        else
            Invalidate();
    }

    private void HandleAuthorizationLost()
    {
        void Invalidate()
        {
            SetProgress(null);
            SetSessionRetryNeeded(false);
            SetSessionRecoveryNeeded(true);
            Error = "Telegram завершил авторизацию этой сессии. Очистите локальную сессию и войдите заново.";
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke((Action)Invalidate);
        else
            Invalidate();
    }

    private string _phoneNumber = "+7";
    private bool _isApiConfigured;
    private bool _isEditingApiCredentials;
    private bool _isApiCredentialsDraftValid;
    private bool _isConfirmationDraftValid;
    private bool _isApiCredentialsCorrupted;
    private bool _isSessionRecoveryNeeded;
    private bool _isSessionRetryNeeded;
    private TelegramLoginProgress? _progress;
    private bool _isBusy;
    private string _error = string.Empty;
    private string _resendStatus = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PhoneNumber
    {
        get => _phoneNumber;
        set { if (_phoneNumber == value) return; _phoneNumber = value; Changed(); Changed(nameof(CanStart)); Changed(nameof(IsPhoneComplete)); }
    }

    public bool IsApiConfigured => _isApiConfigured;
    public bool IsEditingApiCredentials => _isEditingApiCredentials;
    // Редактировать ключи можно и при действующей сессии.
    // Сброс будет выполнен только при фактическом изменении ключей.
    public bool CanEditApiCredentials => IsApiConfigured && !IsBusy && !IsAwaitingInput;
    public bool IsSessionRecoveryNeeded => _isSessionRecoveryNeeded;
    public bool IsSessionRetryNeeded => _isSessionRetryNeeded;
    public bool CanRetrySession => IsApiConfigured && IsSessionRetryNeeded && !IsBusy;
    public bool CanResetTelegramSession => IsApiConfigured &&
        (IsAuthorized || IsSessionRecoveryNeeded) && !IsBusy && !IsAwaitingInput;
    public bool IsApiCredentialsCorrupted => _isApiCredentialsCorrupted;
    public bool CanDeleteApiCredentials => (IsApiConfigured || IsApiCredentialsCorrupted) &&
        !IsBusy && !IsAwaitingInput;
    public bool CanSaveApiCredentials => !IsBusy && _isApiCredentialsDraftValid;
    // A replacement of previously saved API keys must be explicitly confirmed,
    // even when Telegram authorization has not been restored or was lost.
    // The first save and re-saving identical keys must not show a warning.
    public bool ShouldConfirmApiCredentialsChange(bool credentialsChanged) =>
        IsApiConfigured && credentialsChanged;
    // Текст только для отображения под полями. Значения API Hash здесь не сохраняем.
    public string ApiIdDraftError { get; private set; } = string.Empty;
    public string ApiHashDraftError { get; private set; } = string.Empty;

    /// <summary>
    /// Проверяем черновик тем же FluentValidation-валидатором, который применяет
    /// декоратор при выполнении команды. Hash в состоянии ViewModel не храним.
    /// </summary>
    public void UpdateApiCredentialsDraftValidity(string apiId, string apiHash)
    {
        using var scope = scopeFactory.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<
            IValidator<SaveTelegramApiCredentialsCommand>>();
        var command = new SaveTelegramApiCredentialsCommand(apiId.Trim(), apiHash.Trim());
        var validation = validator.Validate(command);
        ApiIdDraftError = validation.Errors.FirstOrDefault(
            issue => issue.PropertyName == nameof(SaveTelegramApiCredentialsCommand.ApiId))?.ErrorMessage
            ?? string.Empty;
        ApiHashDraftError = validation.Errors.FirstOrDefault(
            issue => issue.PropertyName == nameof(SaveTelegramApiCredentialsCommand.ApiHash))?.ErrorMessage
            ?? string.Empty;
        var valid = validation.IsValid;
        if (_isApiCredentialsDraftValid == valid) return;
        _isApiCredentialsDraftValid = valid;
        Changed(nameof(CanSaveApiCredentials));
    }

    public void BeginEditApiCredentials()
    {
        if (!CanEditApiCredentials) return;
        _isEditingApiCredentials = true;
        Error = string.Empty;
        Changed(nameof(IsEditingApiCredentials));
    }

    public void CancelEditApiCredentials()
    {
        _isEditingApiCredentials = false;
        Error = string.Empty;
        Changed(nameof(IsEditingApiCredentials));
    }

    public async Task LoadApiCredentialsStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                IQueryHandler<bool, GetTelegramApiStatusQuery>>();
            var result = await handler.Handle(new GetTelegramApiStatusQuery(), ct);
            if (result.IsFailure)
            {
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return;
            }
            _isApiConfigured = result.Value;
            SetApiCredentialsCorrupted(false);
            Changed(nameof(IsApiConfigured));
            Changed(nameof(CanStart));
            Changed(nameof(CanEditApiCredentials));
            Changed(nameof(CanDeleteApiCredentials));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* ignore */ }
        catch (InvalidDataException)
        {
            _isApiConfigured = false;
            SetApiCredentialsCorrupted(true);
            Changed(nameof(IsApiConfigured));
            Error = "Сохранённые Telegram API-ключи повреждены или недоступны. Удалите локальные ключи и сессию, затем настройте подключение заново.";
        }
        catch (Exception)
        {
            Error = "Не удалось проверить локальные API-ключи Telegram.";
        }
    }

    /// <summary>Проверяет ранее сохранённую сессию без нового кода Telegram.</summary>
    public async Task RestoreSessionAsync(CancellationToken ct = default)
    {
        if (!_isApiConfigured || IsBusy) return;
        IsBusy = true;
        Error = string.Empty;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                IQueryHandler<TelegramLoginProgress, RestoreTelegramSessionQuery>>();
            var result = await handler.Handle(new RestoreTelegramSessionQuery(), ct);
            if (result.IsFailure)
            {
                SetProgress(null);
                var recoveryNeeded = result.Error.Code is
                    "telegram.session_unavailable" or "telegram.session_revoked" or "telegram.session_corrupted";
                SetSessionRecoveryNeeded(recoveryNeeded);
                SetSessionRetryNeeded(!recoveryNeeded);
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return;
            }
            SetSessionRecoveryNeeded(false);
            SetSessionRetryNeeded(false);
            SetProgress(result.Value.IsAuthorized ? result.Value : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /*ignore*/}
        catch (Exception)
        {
            SetProgress(null);
            SetSessionRecoveryNeeded(false);
            SetSessionRetryNeeded(true);
            Error = "Не удалось восстановить Telegram-сессию. Проверьте подключение к Telegram.";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Checks whether the draft differs, without exposing the stored API Hash to the view.
    /// null means the check failed; no confirmation dialog or save should follow.
    /// </summary>
    public async Task<bool?> HaveApiCredentialsChangedAsync(
        string apiId, string apiHash, CancellationToken ct = default)
    {
        if (IsBusy) return null;
        IsBusy = true;
        Error = string.Empty;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                IQueryHandler<bool, HaveTelegramApiCredentialsChangedQuery>>();
            var result = await handler.Handle(
                new HaveTelegramApiCredentialsChangedQuery(apiId.Trim(), apiHash.Trim()), ct);
            if (result.IsFailure)
            {
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return null;
            }
            return result.Value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception)
        {
            Error = "Не удалось сравнить API-ключи с сохранёнными. Проверьте доступ к хранилищу.";
            return null;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> SaveApiCredentialsAsync(string apiId, string apiHash,
        CancellationToken ct = default)
    {
        if (IsBusy) return false;
        IsBusy = true;
        Error = string.Empty;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                ICommandHandler<bool, SaveTelegramApiCredentialsCommand>>();
            var result = await handler.Handle(
                new SaveTelegramApiCredentialsCommand(apiId.Trim(), apiHash.Trim()), ct);
            if (result.IsFailure)
            {
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return false;
            }

            // true = the local session was invalidated, either by a key change
            // or by pending crash recovery. false = no change and no reset.
            if (result.Value)
            {
                SetProgress(null);
                SetSessionRecoveryNeeded(false);
                SetSessionRetryNeeded(false);
            }
            _isApiConfigured = true;
            SetApiCredentialsCorrupted(false);
            _isEditingApiCredentials = false;
            Changed(nameof(IsApiConfigured));
            Changed(nameof(IsEditingApiCredentials));
            Changed(nameof(CanStart));
            Changed(nameof(CanEditApiCredentials));
            Changed(nameof(CanDeleteApiCredentials));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (TelegramApiCredentialsUpdateException exception)
        {
            // Reset may already have disposed the client, even if old keys were restored.
            SetProgress(null);
            SetSessionRecoveryNeeded(true);
            await LoadApiCredentialsStatusAsync(CancellationToken.None);
            Error = exception.OperationCommitted
                ? "Новые API-ключи сохранены и сессия сброшена, но не удалось завершить очистку служебных файлов. Перезапустите ChatPulse."
                : exception.PreviousCredentialsRestored
                    ? "Не удалось сбросить Telegram-сессию. Предыдущие API-ключи восстановлены. Проверьте авторизацию."
                    : "Не удалось завершить замену ключей или восстановить старые. Проверьте настройки подключения Telegram.";
            return false;
        }
        catch (Exception)
        {
            // Error before the reset means that the existing authorization remains valid.
            Error = "Не удалось сохранить API-ключи. Действующая сессия не сброшена.";
            return false;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Deletes only the local secrets and session after explicit UI confirmation.
    /// The Telegram-side device session can be revoked separately in Telegram settings.
    /// </summary>
    public async Task<bool> DeleteApiCredentialsAsync(CancellationToken ct = default)
    {
        if (!CanDeleteApiCredentials) return false;
        IsBusy = true;
        Error = string.Empty;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                ICommandHandler<bool, DeleteTelegramApiCredentialsCommand>>();
            var result = await handler.Handle(new DeleteTelegramApiCredentialsCommand(), ct);
            if (result.IsFailure)
            {
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return false;
            }

            _isApiConfigured = false;
            SetApiCredentialsCorrupted(false);
            _isEditingApiCredentials = false;
            _isApiCredentialsDraftValid = false;
            SetProgress(null);
            SetSessionRecoveryNeeded(false);
            SetSessionRetryNeeded(false);
            Changed(nameof(IsApiConfigured));
            Changed(nameof(IsEditingApiCredentials));
            Changed(nameof(CanStart));
            Changed(nameof(CanSaveApiCredentials));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (TelegramApiCredentialsUpdateException exception)
        {
            // The reset might have invalidated the in-memory authorization already.
            SetProgress(null);
            SetSessionRecoveryNeeded(true);
            await LoadApiCredentialsStatusAsync(CancellationToken.None);
            Error = exception.OperationCommitted
                ? "API-ключи и локальная сессия удалены, но не удалось завершить очистку служебных файлов. Перезапустите ChatPulse."
                : exception.PreviousCredentialsRestored
                    ? "Не удалось сбросить сессию. Ранее сохранённые API-ключи восстановлены."
                    : "Не удалось сбросить сессию и восстановить API-ключи. Проверьте настройки Telegram.";
            return false;
        }
        catch (Exception)
        {
            // Local session is untouched when secret-file deletion fails.
            Error = "Не удалось удалить API-ключи. Сессия не сброшена.";
            return false;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Локально удаляет Telegram-сессию, но не трогает API ID / API Hash.
    /// На серверах Telegram авторизация не отзывается.
    /// </summary>
    public async Task<bool> ResetTelegramSessionAsync(CancellationToken ct = default)
    {
        if (!CanResetTelegramSession) return false;
        IsBusy = true;
        Error = string.Empty;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                ICommandHandler<bool, ResetTelegramSessionCommand>>();
            var result = await handler.Handle(new ResetTelegramSessionCommand(), ct);
            if (result.IsFailure)
            {
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return false;
            }

            SetProgress(null);
            SetSessionRecoveryNeeded(false);
            SetSessionRetryNeeded(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception)
        {
            SetSessionRecoveryNeeded(true);
            // Reset disposes the client before deleting files; a failed file operation
            // must not leave the UI claiming that the old authorization is verified.
            SetProgress(null);
            Error = "Не удалось полностью сбросить локальную Telegram-сессию. Проверьте папку ChatPulse и авторизуйтесь снова.";
            return false;
        }
        finally { IsBusy = false; }
    }

    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Changed(); Changed(nameof(CanStart)); Changed(nameof(CanConfirm)); Changed(nameof(CanResendCode)); Changed(nameof(CanSaveApiCredentials)); Changed(nameof(CanEditApiCredentials)); Changed(nameof(CanDeleteApiCredentials)); Changed(nameof(CanResetTelegramSession)); Changed(nameof(CanCancelLogin)); Changed(nameof(CanRetrySession)); } }
    public bool IsAuthorized => _progress?.IsAuthorized == true;
    public bool IsAwaitingInput => _progress is { RequiredInput: not TelegramLoginInputKind.None };
    public bool CanCancelLogin => IsAwaitingInput && !IsBusy;
    // UI readiness only; the request is still validated by the CQRS decorator.
    public bool IsPhoneComplete => PhoneNumber.Length == 12 &&
        PhoneNumber.StartsWith("+7", StringComparison.Ordinal) &&
        PhoneNumber.Skip(2).All(static character => character is >= '0' and <= '9');
    public bool CanStart => IsApiConfigured && !IsBusy && !IsAuthorized && IsPhoneComplete &&
        !IsAwaitingInput && !IsSessionRecoveryNeeded && !IsSessionRetryNeeded;
    public bool CanConfirm => !IsBusy && IsAwaitingInput && _isConfirmationDraftValid;

    // Только состояние валидации: введённый код, email или пароль не храним в ViewModel.
    // Используем тот же FluentValidation-валидатор, что и CQRS-декоратор отправки.
    public string ConfirmationDraftError { get; private set; } = string.Empty;

    public void UpdateConfirmationDraftValidity(string input)
    {
        using var scope = scopeFactory.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<
            IValidator<ContinueTelegramLoginCommand>>();
        var validation = validator.Validate(new ContinueTelegramLoginCommand(input));
        ConfirmationDraftError = validation.Errors.FirstOrDefault()?.ErrorMessage ?? string.Empty;
        var valid = validation.IsValid;
        if (_isConfirmationDraftValid == valid) return;
        _isConfirmationDraftValid = valid;
        Changed(nameof(CanConfirm));
    }

    public bool IsVerificationCodePending =>
        _progress?.RequiredInput == TelegramLoginInputKind.VerificationCode;
    public bool HasResendOption => IsVerificationCodePending && _progress!.HasResendOption;
    private int SecondsUntilResend => _progress?.ResendAvailableAtUtc is { } at
        ? Math.Max(0, (int)Math.Ceiling((at - DateTimeOffset.UtcNow).TotalSeconds))
        : 0;
    public bool CanResendCode => HasResendOption && SecondsUntilResend == 0 && !IsBusy;
    public string ResendButtonText
    {
        get
        {
            var remaining = SecondsUntilResend;
            return remaining > 0
                ? $"Повторить запрос через {remaining / 60:00}:{remaining % 60:00}"
                : "Отправить код повторно";
        }
    }
    public string ResendStatus { get => _resendStatus; private set { _resendStatus = value; Changed(); Changed(nameof(HasResendStatus)); } }
    public bool HasResendStatus => ResendStatus.Length > 0;

    // Вызывается UI-таймером; он не отправляет запросов в Telegram.
    public void RefreshResendCountdown()
    {
        Changed(nameof(CanResendCode));
        Changed(nameof(ResendButtonText));
    }
    public bool IsPasswordRequired => _progress?.RequiredInput == TelegramLoginInputKind.Password;
    public string ConnectedAccount => _progress?.DisplayName ?? string.Empty;
    public string ConnectedAccountInitials => BuildAccountInitials(ConnectedAccount);
    public string Error { get => _error; private set { _error = value; Changed(); Changed(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;

    public string Prompt => _progress?.RequiredInput switch
    {
        TelegramLoginInputKind.VerificationCode => "Код подтверждения",
        TelegramLoginInputKind.Password => "Пароль двухэтапной проверки",
        TelegramLoginInputKind.Email => "Адрес электронной почты",
        TelegramLoginInputKind.EmailVerificationCode => "Код из электронной почты",
        _ => string.Empty
    };

    public string PromptHint => _progress?.RequiredInput switch
    {
        TelegramLoginInputKind.VerificationCode => _progress?.CodeDelivery switch
        {
            TelegramCodeDeliveryKind.TelegramApp =>
                "Telegram указал отправку кода в приложение. Проверьте служебный чат «Telegram» на уже подключённых устройствах. Фактическая доставка не подтверждена.",
            TelegramCodeDeliveryKind.Sms =>
                "Telegram указал отправку по SMS. Проверьте сообщения для указанного номера. Фактическая доставка не подтверждена.",
            TelegramCodeDeliveryKind.Email =>
                "Telegram указал отправку кода на электронную почту, привязанную к аккаунту. Проверьте почту и папку «Спам».",
            TelegramCodeDeliveryKind.Call =>
                "Telegram указал подтверждение по телефону. Проверьте входящие и пропущенные вызовы для указанного номера.",
            TelegramCodeDeliveryKind.Other =>
                "Telegram выбрал другой способ доставки. ChatPulse пока не определяет его автоматически. Не запрашивайте код многократно.",
            _ =>
                "Telegram ожидает код, но канал доставки не определён. Проверьте номер аккаунта и служебный чат «Telegram»."
        },
        TelegramLoginInputKind.Password => "Введите облачный пароль Telegram, не пароль от телефона.",
        TelegramLoginInputKind.Email => "Telegram запросил адрес почты для дополнительного подтверждения.",
        TelegramLoginInputKind.EmailVerificationCode => "Проверьте почту и введите полученный код.",
        _ => string.Empty
    };

    /// <summary>Удаляет только незавершённую локальную сессию, API-ключи не меняет.</summary>
    public async Task<bool> CancelLoginAsync(CancellationToken ct = default)
    {
        if (!CanCancelLogin) return false;
        IsBusy = true;
        Error = string.Empty;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                ICommandHandler<bool, CancelTelegramLoginCommand>>();
            var result = await handler.Handle(new CancelTelegramLoginCommand(), ct);
            if (result.IsFailure)
            {
                // The client has no pending login; do not strand the user on
                // a confirmation form that cannot be completed or canceled.
                if (result.Error.Code == "telegram.login_not_pending")
                    SetProgress(null);
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return false;
            }
            SetProgress(null);
            SetSessionRecoveryNeeded(false);
            SetSessionRetryNeeded(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception)
        {
            // При ошибке удаления клиент мог быть уже закрыт; не показываем старый шаг входа.
            SetProgress(null);
            SetSessionRecoveryNeeded(true);
            Error = "Не удалось отменить вход полностью. Очистите локальную сессию и повторите попытку.";
            return false;
        }
        finally { IsBusy = false; }
    }

    public async Task BeginAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = string.Empty;
        ResendStatus = string.Empty;
        try
        {
            // Никакой дублирующей проверки номера здесь нет: валидатор запускается декоратором.
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TelegramLoginProgress, BeginTelegramLoginCommand>>();
            var result = await handler.Handle(new BeginTelegramLoginCommand(PhoneNumber.Trim()), ct);
            if (result.IsFailure)
            {
                HandleInterruptedRemoteRequest(result.Error.Code);
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return;
            }
            SetProgress(result.Value);
            SetSessionRecoveryNeeded(false);
            SetSessionRetryNeeded(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /*ignore*/}
        catch (Exception) { Error = "Не удалось выполнить вход. Попробуйте ещё раз."; }
        finally { IsBusy = false; }
    }

    public async Task ResendCodeAsync(CancellationToken ct = default)
    {
        if (!CanResendCode) return;
        IsBusy = true;
        Error = string.Empty;
        ResendStatus = string.Empty;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                ICommandHandler<TelegramLoginProgress, ResendTelegramCodeCommand>>();
            var result = await handler.Handle(new ResendTelegramCodeCommand(), ct);
            if (result.IsFailure)
            {
                HandleInterruptedRemoteRequest(result.Error.Code);
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return;
            }
            SetProgress(result.Value);
            ResendStatus = result.Value.RequiredInput == TelegramLoginInputKind.VerificationCode
                ? "Telegram принял повторный запрос. Проверьте указанный выше способ доставки."
                : string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /*ignore*/}
        catch (Exception)
        {
            Error = "Не удалось повторно запросить код. Проверьте подключение к Telegram.";
        }
        finally { IsBusy = false; }
    }

    public async Task ConfirmAsync(string input, CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = string.Empty;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TelegramLoginProgress, ContinueTelegramLoginCommand>>();
            var result = await handler.Handle(new ContinueTelegramLoginCommand(input), ct);
            if (result.IsFailure)
            {
                HandleInterruptedRemoteRequest(result.Error.Code);
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return;
            }

            // WTelegramClient может снова запросить verification_code после
            // неверного кода без исключения. Не оставляем тот же экран молча.
            var previousInput = _progress?.RequiredInput;
            SetProgress(result.Value);
            if (previousInput == TelegramLoginInputKind.VerificationCode &&
                result.Value.RequiredInput == TelegramLoginInputKind.VerificationCode)
            {
                Error = "Telegram повторно запросил код. Проверьте код из последнего сообщения. Не запрашивайте новый без необходимости.";
            }
            else if (previousInput == TelegramLoginInputKind.EmailVerificationCode &&
                     result.Value.RequiredInput == TelegramLoginInputKind.EmailVerificationCode)
            {
                Error = "Telegram повторно запросил код из электронной почты. Проверьте введённые данные.";
            }
            else if (previousInput == TelegramLoginInputKind.Password &&
                     result.Value.RequiredInput == TelegramLoginInputKind.Password)
            {
                Error = "Telegram повторно запросил пароль двухэтапной проверки. Проверьте его правильность.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /*ignore*/}
        catch (Exception) { Error = "Не удалось подтвердить вход. Попробуйте ещё раз."; }
        finally { IsBusy = false; }
    }

    private void HandleInterruptedRemoteRequest(string code)
    {
        if (code is "telegram.request_timeout" or "telegram.network_unavailable")
        {
            // Клиент был отсоединён, старый шаг входа больше недействителен.
            SetProgress(null);
            SetSessionRecoveryNeeded(false);
            SetSessionRetryNeeded(true);
        }
        else if (code is "telegram.session_revoked" or "telegram.session_cleanup_failed")
        {
            SetProgress(null);
            SetSessionRetryNeeded(false);
            SetSessionRecoveryNeeded(true);
        }
        else if (code is "telegram.account_missing" or "telegram.unsupported_step")
        {
            // The unsupported WTelegram login state was cleared in the client.
            // Do not leave the previous code/password input on screen.
            SetProgress(null);
            SetSessionRecoveryNeeded(false);
            SetSessionRetryNeeded(false);
        }
    }

    private void SetApiCredentialsCorrupted(bool corrupted)
    {
        if (_isApiCredentialsCorrupted == corrupted) return;
        _isApiCredentialsCorrupted = corrupted;
        Changed(nameof(IsApiCredentialsCorrupted));
        Changed(nameof(CanDeleteApiCredentials));
    }

    private void SetSessionRecoveryNeeded(bool needed)
    {
        if (_isSessionRecoveryNeeded == needed) return;
        _isSessionRecoveryNeeded = needed;
        Changed(nameof(IsSessionRecoveryNeeded));
        Changed(nameof(CanResetTelegramSession));
        Changed(nameof(CanStart));
    }

    private void SetSessionRetryNeeded(bool needed)
    {
        if (_isSessionRetryNeeded == needed) return;
        _isSessionRetryNeeded = needed;
        Changed(nameof(IsSessionRetryNeeded));
        Changed(nameof(CanRetrySession));
        Changed(nameof(CanStart));
    }

    private void SetProgress(TelegramLoginProgress? state)
    {
        _progress = state;
        // Новый этап Telegram не должен наследовать валидность ввода с прошлого этапа.
        _isConfirmationDraftValid = false;
        ConfirmationDraftError = string.Empty;
        ResendStatus = string.Empty;
        Changed(nameof(IsAuthorized));
        Changed(nameof(IsAwaitingInput));
        Changed(nameof(CanCancelLogin));
        Changed(nameof(IsPasswordRequired));
        Changed(nameof(CanStart));
        Changed(nameof(CanConfirm));
        Changed(nameof(ConnectedAccount));
        Changed(nameof(ConnectedAccountInitials));
        Changed(nameof(Prompt));
        Changed(nameof(PromptHint));
        Changed(nameof(IsVerificationCodePending));
        Changed(nameof(HasResendOption));
        RefreshResendCountdown();
        Changed(nameof(CanEditApiCredentials));
        Changed(nameof(CanDeleteApiCredentials));
        Changed(nameof(CanResetTelegramSession));
    }

    private static string BuildAccountInitials(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "TG";

        var source = displayName.Trim();
        if (source.StartsWith('@'))
        {
            source = source.TrimStart('@');
            return string.Concat(source.Take(2)).ToUpperInvariant();
        }

        var parts = source
            .Split([' ', '	', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => part.Length > 0)
            .ToArray();

        if (parts.Length >= 2)
        {
            return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
        }

        return string.Concat(parts[0].Take(2)).ToUpperInvariant();
    }

    private void Changed([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
