using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.ViewModels;
using ChatPulse.Desktop.Views.Dialogs;

namespace ChatPulse.Desktop.Views.Onboarding;

public partial class OnboardingStepTwoView : UserControl
{
    private const int LocalPhoneLength = 10;
    private string _localDigits = string.Empty;
    private bool _isFormattingPhone;
    private bool _apiIdValidationVisible;
    private bool _apiHashValidationVisible;
    private bool _confirmationValidationVisible;
    private bool _cloudPasswordValidationVisible;
    private string? _lastConfirmationPrompt;
    private bool _apiHashVisible;
    private bool _cloudPasswordVisible;
    private bool _syncingApiHash;
    private bool _syncingCloudPassword;
    private TelegramOnboardingPage _currentPage = TelegramOnboardingPage.ApiCredentials;
    private readonly DispatcherTimer _resendTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public OnboardingStepTwoView()
    {
        InitializeComponent();
        _resendTimer.Tick += (_, _) =>
        {
            if (DataContext is not TelegramSetupViewModel vm) return;
            vm.RefreshResendCountdown();
            if (vm.CanResendCode || !vm.HasResendOption) _resendTimer.Stop();
        };
        Loaded += (_, _) =>
        {
            if (DataContext is TelegramSetupViewModel vm) UpdateResendTimer(vm);
        };
        Unloaded += (_, _) => _resendTimer.Stop();
        DataObject.AddPastingHandler(PhoneInput, PhoneInput_OnPasting);
        DataObject.AddPastingHandler(ApiIdInput, ApiIdInput_OnPasting);
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is TelegramSetupViewModel oldVm)
                oldVm.PropertyChanged -= ViewModel_OnPropertyChanged;

            if (args.NewValue is TelegramSetupViewModel vm)
            {
                _lastConfirmationPrompt = null;
                vm.PropertyChanged += ViewModel_OnPropertyChanged;
                var stored = vm.PhoneNumber.StartsWith("+7", StringComparison.Ordinal)
                    ? OnlyAsciiDigits(vm.PhoneNumber[2..])
                    : string.Empty;
                SetPhoneDigits(stored[..Math.Min(stored.Length, LocalPhoneLength)], stored.Length);
                UpdatePanels(vm);
                RefreshApiCredentialsSaveState();
            }
        };
        RefreshPhonePlaceholder();
    }

    public event EventHandler? BackRequested;
    public event EventHandler? ContinueRequested;
    public event Action<TelegramOnboardingPage>? TelegramPageRequested;

    public void ShowTelegramPage(TelegramOnboardingPage page)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;

        _currentPage = page == TelegramOnboardingPage.Phone && !vm.IsApiConfigured
            ? TelegramOnboardingPage.ApiCredentials : page;

        // Навигация не должна открывать редактирование сохранённых секретов.
        // Не стираем сообщение об отозванной/непроверенной сессии при переходе с первого чата.
        if (vm.IsEditingApiCredentials)
            vm.CancelEditApiCredentials();
        ClearApiDraft();
        ClearCloudPassword();
        ConfirmationInput.Clear();
        _confirmationValidationVisible = false;
        UpdatePanels(vm);
    }

    // Both fields use the same FluentValidation rules as the command decorator.
    // The password is read only to evaluate the draft and is not retained by the ViewModel.
    private string CurrentApiHash => _apiHashVisible ? ApiHashVisibleInput.Text : ApiHashInput.Password;
    private string CurrentCloudPassword => _cloudPasswordVisible ? CloudPasswordVisibleInput.Text : PasswordInput.Password;

    private void ApiIdInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ApiIdInput.Text.Length > 0) _apiIdValidationVisible = true;
        RefreshApiCredentialsSaveState();
    }

    private void ApiIdInput_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _apiIdValidationVisible = true;
        RefreshApiCredentialsSaveState();
    }

    private void ApiHashInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingApiHash) return;
        if (ApiHashInput.Password.Length > 0) _apiHashValidationVisible = true;
        RefreshApiCredentialsSaveState();
    }

    private void ApiHashVisibleInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingApiHash) return;
        if (ApiHashVisibleInput.Text.Length > 0) _apiHashValidationVisible = true;
        RefreshApiCredentialsSaveState();
    }

    private void ApiHashInput_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Нажатие на глазик не должно внезапно показывать ошибку пустого поля.
        if (e.NewFocus is Button button && button == ApiHashVisibilityButton) return;
        _apiHashValidationVisible = true;
        RefreshApiCredentialsSaveState();
    }

    private void RefreshApiCredentialsSaveState()
    {
        UpdateApiFieldHints();
        if (ApiIdInput is null || ApiHashInput is null || ApiHashVisibleInput is null ||
            ApiIdErrorText is null || ApiHashErrorText is null) return;
        if (DataContext is not TelegramSetupViewModel vm) return;
        vm.UpdateApiCredentialsDraftValidity(ApiIdInput.Text, CurrentApiHash);
        DisplayInlineError(ApiIdErrorText, _apiIdValidationVisible ? vm.ApiIdDraftError : string.Empty);
        DisplayInlineError(ApiHashErrorText, _apiHashValidationVisible ? vm.ApiHashDraftError : string.Empty);
    }

    private void UpdateApiFieldHints()
    {
        // Инициализация XAML и обработчики изменения текста могут вызываться до полной загрузки полей.
        if (ApiIdInput is not null && ApiIdHint is not null)
            ApiIdHint.Visibility = ApiIdInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (ApiHashInput is not null && ApiHashVisibleInput is not null && ApiHashHint is not null)
            ApiHashHint.Visibility = CurrentApiHash.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void DisplayInlineError(TextBlock block, string message)
    {
        if (block is null) return;
        block.Text = message;
        block.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApiHashVisibilityButton_OnClick(object sender, RoutedEventArgs e)
    {
        _syncingApiHash = true;
        try
        {
            if (_apiHashVisible)
            {
                ApiHashInput.Password = ApiHashVisibleInput.Text;
                ApiHashVisibleInput.Clear();
            }
            else
            {
                ApiHashVisibleInput.Text = ApiHashInput.Password;
                ApiHashInput.Clear();
            }
            _apiHashVisible = !_apiHashVisible;
            ApiHashInput.Visibility = _apiHashVisible ? Visibility.Collapsed : Visibility.Visible;
            ApiHashVisibleInput.Visibility = _apiHashVisible ? Visibility.Visible : Visibility.Collapsed;
            ApiHashEyeSlash.Visibility = _apiHashVisible ? Visibility.Collapsed : Visibility.Visible;
            ApiHashVisibilityButton.ToolTip = _apiHashVisible ? "Скрыть API Hash" : "Показать API Hash";
            System.Windows.Automation.AutomationProperties.SetName(
                ApiHashVisibilityButton, _apiHashVisible ? "Скрыть API Hash" : "Показать API Hash");
        }
        finally { _syncingApiHash = false; }
        if (_apiHashVisible) ApiHashVisibleInput.Focus();
        else ApiHashInput.Focus();
        RefreshApiCredentialsSaveState();
    }

    private void CloudPasswordVisibilityButton_OnClick(object sender, RoutedEventArgs e)
    {
        _syncingCloudPassword = true;
        try
        {
            if (_cloudPasswordVisible)
            {
                PasswordInput.Password = CloudPasswordVisibleInput.Text;
                CloudPasswordVisibleInput.Clear();
            }
            else
            {
                CloudPasswordVisibleInput.Text = PasswordInput.Password;
                PasswordInput.Clear();
            }
        }
        finally { _syncingCloudPassword = false; }
        _cloudPasswordVisible = !_cloudPasswordVisible;
        PasswordInput.Visibility = _cloudPasswordVisible ? Visibility.Collapsed : Visibility.Visible;
        CloudPasswordVisibleInput.Visibility = _cloudPasswordVisible ? Visibility.Visible : Visibility.Collapsed;
        CloudPasswordEyeSlash.Visibility = _cloudPasswordVisible ? Visibility.Collapsed : Visibility.Visible;
        CloudPasswordVisibilityButton.ToolTip = _cloudPasswordVisible ? "Скрыть пароль" : "Показать пароль";
        System.Windows.Automation.AutomationProperties.SetName(
            CloudPasswordVisibilityButton, _cloudPasswordVisible ? "Скрыть пароль" : "Показать пароль");
        if (_cloudPasswordVisible) CloudPasswordVisibleInput.Focus();
        else PasswordInput.Focus();
        RefreshConfirmationDraftState();
    }

    private void ClearApiHash()
    {
        _syncingApiHash = true;
        try
        {
            ApiHashInput.Clear();
            ApiHashVisibleInput.Clear();
        }
        finally { _syncingApiHash = false; }
        _apiHashVisible = false;
        _apiHashValidationVisible = false;
        ApiHashInput.Visibility = Visibility.Visible;
        ApiHashVisibleInput.Visibility = Visibility.Collapsed;
        ApiHashEyeSlash.Visibility = Visibility.Visible;
        ApiHashVisibilityButton.ToolTip = "Показать API Hash";
        System.Windows.Automation.AutomationProperties.SetName(ApiHashVisibilityButton, "Показать API Hash");
        RefreshApiCredentialsSaveState();
    }

    private void ConfirmationInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConfirmationInput.Text.Length > 0) _confirmationValidationVisible = true;
        RefreshConfirmationDraftState();
    }

    private void ConfirmationInput_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _confirmationValidationVisible = true;
        RefreshConfirmationDraftState();
    }

    private void CloudPasswordInput_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingCloudPassword) return;
        if (CurrentCloudPassword.Length > 0) _cloudPasswordValidationVisible = true;
        RefreshConfirmationDraftState();
    }

    private void CloudPasswordVisibleInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingCloudPassword) return;
        if (CurrentCloudPassword.Length > 0) _cloudPasswordValidationVisible = true;
        RefreshConfirmationDraftState();
    }

    private void CloudPasswordInput_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is Button button && button == CloudPasswordVisibilityButton) return;
        _cloudPasswordValidationVisible = true;
        RefreshConfirmationDraftState();
    }

    private void RefreshConfirmationDraftState()
    {
        if (ConfirmationInput is null || ConfirmationErrorText is null ||
            PasswordInput is null || CloudPasswordVisibleInput is null || CloudPasswordErrorText is null)
            return;
        if (DataContext is not TelegramSetupViewModel vm) return;

        vm.UpdateConfirmationDraftValidity(vm.IsPasswordRequired
            ? CurrentCloudPassword : ConfirmationInput.Text);
        DisplayInlineError(ConfirmationErrorText,
            !vm.IsPasswordRequired && _confirmationValidationVisible && vm.IsAwaitingInput
                ? vm.ConfirmationDraftError : string.Empty);
        DisplayInlineError(CloudPasswordErrorText,
            vm.IsPasswordRequired && _cloudPasswordValidationVisible && vm.IsAwaitingInput
                ? vm.ConfirmationDraftError : string.Empty);
    }

    private void ClearCloudPassword()
    {
        _cloudPasswordValidationVisible = false;
        DisplayInlineError(CloudPasswordErrorText, string.Empty);
        PasswordInput.Clear();
        CloudPasswordVisibleInput.Clear();
        _cloudPasswordVisible = false;
        PasswordInput.Visibility = Visibility.Visible;
        CloudPasswordVisibleInput.Visibility = Visibility.Collapsed;
        CloudPasswordEyeSlash.Visibility = Visibility.Visible;
        CloudPasswordVisibilityButton.ToolTip = "Показать пароль";
        System.Windows.Automation.AutomationProperties.SetName(CloudPasswordVisibilityButton, "Показать пароль");
    }

    private void ClearApiDraft()
    {
        _apiIdValidationVisible = false;
        _apiHashValidationVisible = false;
        ApiIdInput.Clear();
        ClearApiHash();
        RefreshApiCredentialsSaveState();
    }

    // Restrict what the TextBox accepts; semantic API ID validation remains in FluentValidation.
    private void ApiIdInput_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0 || !e.Text.All(IsAsciiDigit))
            e.Handled = true;
    }

    private void ApiIdInput_OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var value = e.DataObject.GetData(DataFormats.UnicodeText) as string
                    ?? e.DataObject.GetData(DataFormats.Text) as string;
        if (value is null)
        {
            e.CancelCommand();
            return;
        }

        // Trim incidental whitespace from copied Telegram values, but reject mixed content.
        var digits = value.Trim();
        if (digits.Length == 0 || !digits.All(IsAsciiDigit) ||
            ApiIdInput.Text.Length - ApiIdInput.SelectionLength + digits.Length > ApiIdInput.MaxLength)
        {
            e.CancelCommand();
            return;
        }

        e.CancelCommand();
        ApiIdInput.SelectedText = digits;
    }

    // UI input mask only: +7 is non-editable and WTelegram receives +7 plus exactly 10 digits.
    // The actual input validation is performed by FluentValidation through ValidationDecorator.
    private void PhoneInput_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1 || !IsAsciiDigit(e.Text[0]))
        {
            e.Handled = true;
            return;
        }
        var selected = DigitsInSelectedText();
        if (_localDigits.Length - selected >= LocalPhoneLength)
            e.Handled = true;
    }

    private void PhoneInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isFormattingPhone) return;
        var caretPosition = Math.Min(PhoneInput.CaretIndex, PhoneInput.Text.Length);
        var digitsBeforeCaret = OnlyAsciiDigits(PhoneInput.Text[..caretPosition]).Length;
        var digits = OnlyAsciiDigits(PhoneInput.Text);
        SetPhoneDigits(digits[..Math.Min(digits.Length, LocalPhoneLength)], digitsBeforeCaret);
    }

    private void PhoneInput_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Back or Key.Delete)) return;

        // Remove digits, not only formatting characters such as spaces or dashes.
        var start = DigitsBeforeCaret(PhoneInput.SelectionStart);
        var selectedCount = DigitsInSelectedText();
        if (selectedCount > 0)
        {
            SetPhoneDigits(_localDigits.Remove(start, selectedCount), start);
        }
        else if (e.Key == Key.Back && start > 0)
        {
            SetPhoneDigits(_localDigits.Remove(start - 1, 1), start - 1);
        }
        else if (e.Key == Key.Delete && start < _localDigits.Length)
        {
            SetPhoneDigits(_localDigits.Remove(start, 1), start);
        }
        e.Handled = true;
    }

    private void PhoneInput_OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var raw = e.DataObject.GetData(DataFormats.UnicodeText) as string
                  ?? e.DataObject.GetData(DataFormats.Text) as string;
        e.CancelCommand();
        if (string.IsNullOrEmpty(raw)) return;

        var digits = OnlyAsciiDigits(raw);
        // Support both +7 (999) 123-45-67 and 8 (999) 123-45-67 when pasting.
        if (digits.Length == 11 && (digits[0] == '7' || digits[0] == '8'))
            digits = digits[1..];
        if (digits.Length == 0 || digits.Length > LocalPhoneLength) return;
        if (raw.Any(char.IsLetter)) return;

        var start = DigitsBeforeCaret(PhoneInput.SelectionStart);
        var selected = DigitsInSelectedText();
        if (_localDigits.Length - selected + digits.Length > LocalPhoneLength) return;

        var updated = _localDigits.Remove(start, selected).Insert(start, digits);
        SetPhoneDigits(updated, start + digits.Length);
    }

    private void PhoneInput_OnFocusChanged(object sender, RoutedEventArgs e)
        => RefreshPhonePlaceholder();

    private void SetPhoneDigits(string digits, int caretDigitCount)
    {
        _localDigits = digits;
        var formatted = FormatLocalNumber(digits);
        _isFormattingPhone = true;
        try
        {
            PhoneInput.Text = formatted;
            PhoneInput.CaretIndex = CaretPositionAfterDigits(formatted, caretDigitCount);
        }
        finally
        {
            _isFormattingPhone = false;
        }

        // VM stores the canonical E.164 representation, not formatting characters.
        if (DataContext is TelegramSetupViewModel vm)
            vm.PhoneNumber = "+7" + digits;

        RefreshPhonePlaceholder();
    }

    private void RefreshPhonePlaceholder()
    {
        PhonePlaceholder.Visibility = _localDigits.Length == 0 && !PhoneInput.IsKeyboardFocusWithin
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private int DigitsBeforeCaret(int textPosition)
        => OnlyAsciiDigits(PhoneInput.Text[..Math.Min(PhoneInput.Text.Length, textPosition)]).Length;

    private int DigitsInSelectedText()
    {
        if (PhoneInput.SelectionLength == 0) return 0;
        return OnlyAsciiDigits(PhoneInput.SelectedText).Length;
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static string OnlyAsciiDigits(string value)
        => new(value.Where(IsAsciiDigit).ToArray());

    private static string FormatLocalNumber(string digits)
    {
        var formatted = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i == 0) formatted.Append('(');
            if (i == 3) formatted.Append(") ");
            if (i is 6 or 8) formatted.Append('-');
            formatted.Append(digits[i]);
        }
        return formatted.ToString();
    }

    private static int CaretPositionAfterDigits(string formatted, int digitCount)
    {
        if (digitCount <= 0) return formatted.StartsWith('(') ? 1 : 0;
        var seen = 0;
        for (var i = 0; i < formatted.Length; i++)
        {
            if (!IsAsciiDigit(formatted[i])) continue;
            if (++seen == digitCount) return i + 1;
        }
        return formatted.Length;
    }

    private async void RetryRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm || !vm.CanRetrySession) return;
        await vm.RestoreSessionAsync();
        UpdatePanels(vm);
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        await vm.BeginAsync();
        UpdatePanels(vm);
    }

    private async void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        RefreshConfirmationDraftState();
        if (!vm.CanConfirm)
        {
            if (vm.IsPasswordRequired) _cloudPasswordValidationVisible = true;
            else _confirmationValidationVisible = true;
            RefreshConfirmationDraftState();
            return;
        }

        var input = vm.IsPasswordRequired ? CurrentCloudPassword : ConfirmationInput.Text;
        ClearCloudPassword();
        ConfirmationInput.Clear();
        _confirmationValidationVisible = false;
        await vm.ConfirmAsync(input);
        UpdatePanels(vm);
    }

    private async void ResendCodeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm || !vm.CanResendCode) return;
        ConfirmationInput.Clear();
        _confirmationValidationVisible = false;
        await vm.ResendCodeAsync();
        UpdatePanels(vm);
    }

    private async void CancelLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm || !vm.CanCancelLogin) return;
        if (!ShowModal(new TelegramLoginInProgressDialog())) return;
        await CancelPendingLoginAsync(vm);
    }

    private async Task CancelPendingLoginAsync(TelegramSetupViewModel vm)
    {
        ConfirmationInput.Clear();
        _confirmationValidationVisible = false;
        ClearCloudPassword();
        await vm.CancelLoginAsync();
        UpdatePanels(vm);
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TelegramSetupViewModel vm &&
            e.PropertyName is nameof(TelegramSetupViewModel.IsApiConfigured) or
                              nameof(TelegramSetupViewModel.IsEditingApiCredentials) or
                              nameof(TelegramSetupViewModel.IsBusy) or
                              nameof(TelegramSetupViewModel.IsAwaitingInput) or
                              nameof(TelegramSetupViewModel.IsPasswordRequired) or
                              nameof(TelegramSetupViewModel.IsSessionRetryNeeded) or
                              nameof(TelegramSetupViewModel.IsSessionRecoveryNeeded) or
                              nameof(TelegramSetupViewModel.IsAuthorized))
        {
            UpdatePanels(vm);
        }
    }

    private void UpdatePanels(TelegramSetupViewModel vm)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => UpdatePanels(vm)); return; }
        // При переходе от кода к паролю/email или выходе из авторизации
        // не переносим введённый секрет и отметки об ошибках на новый этап.
        if (_lastConfirmationPrompt != vm.Prompt)
        {
            _lastConfirmationPrompt = vm.Prompt;
            _confirmationValidationVisible = false;
            ConfirmationInput.Clear();
            ClearCloudPassword();
        }
        var showApi = _currentPage == TelegramOnboardingPage.ApiCredentials || !vm.IsApiConfigured;
        ApiKeysPanel.Visibility = showApi ? Visibility.Visible : Visibility.Collapsed;
        ApiCredentialsEntryPanel.Visibility = !vm.IsApiConfigured || vm.IsEditingApiCredentials
            ? Visibility.Visible : Visibility.Collapsed;
        ApiSavedPanel.Visibility = vm.IsApiConfigured && !vm.IsEditingApiCredentials
            ? Visibility.Visible : Visibility.Collapsed;
        // Until the old session is verified or explicitly reset, do not offer
        // a phone login that CanStart intentionally forbids for safety.
        PhonePanel.Visibility = !showApi && !vm.IsAwaitingInput && !vm.IsAuthorized &&
                                !vm.IsSessionRetryNeeded && !vm.IsSessionRecoveryNeeded
            ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPanel.Visibility = !showApi && vm.IsAwaitingInput
            ? Visibility.Visible : Visibility.Collapsed;
        ConnectedPanel.Visibility = !showApi && vm.IsAuthorized
            ? Visibility.Visible : Visibility.Collapsed;
        var showApiCancel = vm.IsApiConfigured && vm.IsEditingApiCredentials;
        ApiCancelButton.Visibility = showApiCancel ? Visibility.Visible : Visibility.Collapsed;
        ApiCancelColumn.Width = showApiCancel ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ApiSaveButton.SetValue(Grid.ColumnSpanProperty, showApiCancel ? 1 : 2);
        ApiSaveButton.Margin = showApiCancel ? new Thickness(0, 0, 6, 0) : new Thickness(0);
        ApiCancelButton.Margin = showApiCancel ? new Thickness(6, 0, 0, 0) : new Thickness(0);
        CloudPasswordContainer.Visibility = vm.IsPasswordRequired ? Visibility.Visible : Visibility.Collapsed;
        ConfirmationInput.Visibility = vm.IsPasswordRequired ? Visibility.Collapsed : Visibility.Visible;
        if (!vm.IsPasswordRequired && (_cloudPasswordVisible || PasswordInput.Password.Length > 0 ||
                                      CloudPasswordVisibleInput.Text.Length > 0))
            ClearCloudPassword();
        TelegramHelpButton.Visibility = !showApi && !vm.IsAuthorized &&
                                        !vm.IsSessionRetryNeeded && !vm.IsSessionRecoveryNeeded
            ? Visibility.Visible : Visibility.Collapsed;
        // Один переход «Продолжить» для двух подэтапов:
        // сохранённые ключи -> номер Telegram; успешный вход -> первый чат.
        TelegramContinueButton.IsEnabled = !vm.IsBusy &&
            !vm.IsSessionRetryNeeded && !vm.IsSessionRecoveryNeeded &&
            (showApi
                ? vm.IsApiConfigured && !vm.IsEditingApiCredentials
                : vm.IsAuthorized);
        RefreshConfirmationDraftState();
        UpdateResendTimer(vm);
    }

    private void UpdateResendTimer(TelegramSetupViewModel vm)
    {
        if (_currentPage == TelegramOnboardingPage.Phone && IsLoaded &&
            vm.HasResendOption && !vm.CanResendCode && !vm.IsBusy)
        {
            _resendTimer.Start();
        }
        else
        {
            _resendTimer.Stop();
        }
        vm.RefreshResendCountdown();
    }

    private async void ApiKeysSaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        // Confirm any actual replacement of saved credentials, even when the
        // session is unverified, revoked, or not currently connected.
        // Initial save and re-saving identical credentials need no warning.
        var apiId = ApiIdInput.Text.Trim();
        var apiHash = CurrentApiHash.Trim();
        var changed = await vm.HaveApiCredentialsChangedAsync(apiId, apiHash);
        if (changed is null) return;
        // Only save the draft that was compared; a concurrent edit needs rechecking.
        if (ApiIdInput.Text.Trim() != apiId || CurrentApiHash.Trim() != apiHash)
            return;
        if (vm.ShouldConfirmApiCredentialsChange(changed.Value) &&
            !ShowModal(new TelegramSessionActionDialog(TelegramSessionAction.ChangeApiKeys)))
            return;

        var saved = await vm.SaveApiCredentialsAsync(apiId, apiHash);
        if (saved)
        {
            ClearApiDraft();
            TelegramPageRequested?.Invoke(TelegramOnboardingPage.Phone);
        }
        UpdatePanels(vm);
    }

    private async void ApiKeysDeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm || !vm.CanDeleteApiCredentials)
            return;

        if (!ShowModal(new DeleteTelegramApiKeysDialog())) return;

        if (!await vm.DeleteApiCredentialsAsync()) return;

        ClearApiDraft();
        vm.CancelEditApiCredentials();
        TelegramPageRequested?.Invoke(TelegramOnboardingPage.ApiCredentials);
        UpdatePanels(vm);
    }

    private async void ResetTelegramSessionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm || !vm.CanResetTelegramSession)
            return;

        if (!ShowModal(new TelegramSessionActionDialog(TelegramSessionAction.ResetSession)))
            return;

        if (!await vm.ResetTelegramSessionAsync())
        {
            UpdatePanels(vm);
            return;
        }

        ConfirmationInput.Clear();
        ClearCloudPassword();
        UpdatePanels(vm);
    }

    private void ApiKeysEditButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        ClearApiDraft();
        vm.BeginEditApiCredentials();
        UpdatePanels(vm);
    }

    private void ApiKeysCancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        ClearApiDraft();
        vm.CancelEditApiCredentials();
        UpdatePanels(vm);
    }

    private void ApiKeysHelpButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowHelp(new TelegramApiKeysHelpDialog());
    }

    private void HelpButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowHelp(new TelegramHelpDialog());
    }

    private void ShowHelp(Window dialog) => ShowModal(dialog);

    private bool ShowModal(Window dialog)
    {
        var owner = Window.GetWindow(this);
        dialog.Owner = owner;

        if (owner is MainWindow mainWindow)
        {
            mainWindow.ShowDialogOverlay();
            try { return dialog.ShowDialog() == true; }
            finally { mainWindow.HideDialogOverlay(); }
        }

        return dialog.ShowDialog() == true;
    }

    private async void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm || vm.IsBusy) return;

        if (_currentPage == TelegramOnboardingPage.Phone)
        {
            // При незавершённой проверке кода нельзя тихо показывать поле номера:
            // состояние авторизации осталось бы активным в WTelegramClient.
            if (vm.IsAwaitingInput)
            {
                if (ShowModal(new TelegramLoginInProgressDialog()))
                    await CancelPendingLoginAsync(vm);
                return;
            }
            TelegramPageRequested?.Invoke(TelegramOnboardingPage.ApiCredentials);
            return;
        }

        // На странице ключей «Назад» всегда возвращает к имени.
        // Черновик секретов очищаем, сохранённые ключи при этом не меняются.
        ClearApiDraft();
        vm.CancelEditApiCredentials();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm || vm.IsBusy ||
            vm.IsSessionRetryNeeded || vm.IsSessionRecoveryNeeded) return;

        if (_currentPage == TelegramOnboardingPage.ApiCredentials)
        {
            if (vm.IsApiConfigured && !vm.IsEditingApiCredentials)
                TelegramPageRequested?.Invoke(TelegramOnboardingPage.Phone);
            return;
        }

        if (_currentPage == TelegramOnboardingPage.Phone && vm.IsAuthorized)
            ContinueRequested?.Invoke(this, EventArgs.Empty);
    }
}
