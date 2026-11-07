using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Get;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Save;
using ChatPulse.Application.Features.Telegram.BeginLogin;
using ChatPulse.Application.Features.Telegram.ContinueLogin;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Shared.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ChatPulse.Desktop.ViewModels;

public sealed class TelegramSetupViewModel(IServiceScopeFactory scopeFactory)
    : INotifyPropertyChanged
{
    private string _phoneNumber = "+7";
    private bool _isApiConfigured;
    private bool _isEditingApiCredentials;
    private TelegramLoginProgress? _progress;
    private bool _isBusy;
    private string _error = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PhoneNumber
    {
        get => _phoneNumber;
        set { if (_phoneNumber == value) return; _phoneNumber = value; Changed(); Changed(nameof(CanStart)); Changed(nameof(IsPhoneComplete)); }
    }

    public bool IsApiConfigured => _isApiConfigured;
    public bool IsEditingApiCredentials => _isEditingApiCredentials;
    public bool CanEditApiCredentials => IsApiConfigured && !IsBusy && !IsAuthorized && !IsAwaitingInput;
    public bool CanSaveApiCredentials => !IsBusy;

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
        Changed(nameof(IsApiConfigured));
        Changed(nameof(CanStart));
        Changed(nameof(CanEditApiCredentials));
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
            _isApiConfigured = true;
            _isEditingApiCredentials = false;
            Changed(nameof(IsApiConfigured));
            Changed(nameof(IsEditingApiCredentials));
            Changed(nameof(CanStart));
            Changed(nameof(CanEditApiCredentials));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception)
        {
            Error = "Не удалось сохранить API-ключи. Проверьте доступ к папке ChatPulse.";
            return false;
        }
        finally { IsBusy = false; }
    }

    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Changed(); Changed(nameof(CanStart)); Changed(nameof(CanConfirm)); Changed(nameof(CanSaveApiCredentials)); Changed(nameof(CanEditApiCredentials)); } }
    public bool IsAuthorized => _progress?.IsAuthorized == true;
    public bool IsAwaitingInput => _progress is { RequiredInput: not TelegramLoginInputKind.None };
    // UI readiness only; the request is still validated by the CQRS decorator.
    public bool IsPhoneComplete => PhoneNumber.StartsWith("+7", StringComparison.Ordinal) && PhoneNumber.Length == 12;
    public bool CanStart => IsApiConfigured && !IsBusy && !IsAuthorized && IsPhoneComplete && !IsAwaitingInput;
    public bool CanConfirm => !IsBusy && IsAwaitingInput;
    public bool IsPasswordRequired => _progress?.RequiredInput == TelegramLoginInputKind.Password;
    public string ConnectedAccount => _progress?.DisplayName ?? string.Empty;
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
        TelegramLoginInputKind.VerificationCode => "Проверьте служебные сообщения Telegram на другом устройстве.",
        TelegramLoginInputKind.Password => "Введите облачный пароль Telegram, не пароль от телефона.",
        TelegramLoginInputKind.Email => "Telegram запросил адрес почты для дополнительного подтверждения.",
        TelegramLoginInputKind.EmailVerificationCode => "Проверьте почту и введите полученный код.",
        _ => string.Empty
    };

    public async Task BeginAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = string.Empty;
        try
        {
            // Никакой дублирующей проверки номера здесь нет: валидатор запускается декоратором.
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TelegramLoginProgress, BeginTelegramLoginCommand>>();
            var result = await handler.Handle(new BeginTelegramLoginCommand(PhoneNumber.Trim()), ct);
            if (result.IsFailure) { Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message; return; }
            SetProgress(result.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /*ignore*/ }
        catch (Exception) { Error = "Не удалось выполнить вход. Попробуйте ещё раз."; }
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
            if (result.IsFailure) { Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message; return; }
            SetProgress(result.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /*ignore*/}
        catch (Exception) { Error = "Не удалось подтвердить вход. Попробуйте ещё раз."; }
        finally { IsBusy = false; }
    }

    private void SetProgress(TelegramLoginProgress state)
    {
        _progress = state;
        Changed(nameof(IsAuthorized));
        Changed(nameof(IsAwaitingInput));
        Changed(nameof(IsPasswordRequired));
        Changed(nameof(CanStart));
        Changed(nameof(CanConfirm));
        Changed(nameof(ConnectedAccount));
        Changed(nameof(Prompt));
        Changed(nameof(PromptHint));
    }

    private void Changed([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
