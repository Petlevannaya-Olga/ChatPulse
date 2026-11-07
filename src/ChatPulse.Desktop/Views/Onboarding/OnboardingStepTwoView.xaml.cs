using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChatPulse.Desktop.ViewModels;
using ChatPulse.Desktop.Views.Dialogs;

namespace ChatPulse.Desktop.Views.Onboarding;

public partial class OnboardingStepTwoView : UserControl
{
    private const int LocalPhoneLength = 10;
    private string _localDigits = string.Empty;
    private bool _isFormattingPhone;

    public OnboardingStepTwoView()
    {
        InitializeComponent();
        DataObject.AddPastingHandler(PhoneInput, PhoneInput_OnPasting);
        DataObject.AddPastingHandler(ApiIdInput, ApiIdInput_OnPasting);
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is TelegramSetupViewModel oldVm)
                oldVm.PropertyChanged -= ViewModel_OnPropertyChanged;

            if (args.NewValue is TelegramSetupViewModel vm)
            {
                vm.PropertyChanged += ViewModel_OnPropertyChanged;
                var stored = vm.PhoneNumber.StartsWith("+7", StringComparison.Ordinal)
                    ? OnlyAsciiDigits(vm.PhoneNumber[2..])
                    : string.Empty;
                SetPhoneDigits(stored[..Math.Min(stored.Length, LocalPhoneLength)], stored.Length);
                UpdatePanels(vm);
            }
        };
        RefreshPhonePlaceholder();
    }

    public event EventHandler? BackRequested;
    public event EventHandler? ContinueRequested;

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

    private async void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        await vm.BeginAsync();
        UpdatePanels(vm);
    }

    private async void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        var input = vm.IsPasswordRequired ? PasswordInput.Password : ConfirmationInput.Text;
        PasswordInput.Clear();
        ConfirmationInput.Clear();
        await vm.ConfirmAsync(input);
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
                              nameof(TelegramSetupViewModel.IsAuthorized))
        {
            UpdatePanels(vm);
        }
    }

    private void UpdatePanels(TelegramSetupViewModel vm)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => UpdatePanels(vm)); return; }
        var showApi = !vm.IsApiConfigured || vm.IsEditingApiCredentials;
        ApiKeysPanel.Visibility = showApi ? Visibility.Visible : Visibility.Collapsed;
        PhonePanel.Visibility = !showApi && !vm.IsAwaitingInput && !vm.IsAuthorized
            ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPanel.Visibility = !showApi && vm.IsAwaitingInput
            ? Visibility.Visible : Visibility.Collapsed;
        ConnectedPanel.Visibility = !showApi && vm.IsAuthorized
            ? Visibility.Visible : Visibility.Collapsed;
        PhoneEditApiButton.Visibility = vm.CanEditApiCredentials
            ? Visibility.Visible : Visibility.Collapsed;
        ApiCancelButton.Visibility = vm.IsApiConfigured && vm.IsEditingApiCredentials
            ? Visibility.Visible : Visibility.Collapsed;
        PasswordInput.Visibility = vm.IsPasswordRequired ? Visibility.Visible : Visibility.Collapsed;
        ConfirmationInput.Visibility = vm.IsPasswordRequired ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ApiKeysSaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        var saved = await vm.SaveApiCredentialsAsync(ApiIdInput.Text, ApiHashInput.Password);
        if (saved)
        {
            ApiIdInput.Clear();
            ApiHashInput.Clear();
        }
        UpdatePanels(vm);
    }

    private void ApiKeysEditButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        ApiIdInput.Clear();
        ApiHashInput.Clear();
        vm.BeginEditApiCredentials();
        UpdatePanels(vm);
    }

    private void ApiKeysCancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelegramSetupViewModel vm) return;
        ApiIdInput.Clear();
        ApiHashInput.Clear();
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

    private void ShowHelp(Window dialog)
    {
        var owner = Window.GetWindow(this);
        dialog.Owner = owner;

        if (owner is MainWindow mainWindow)
        {
            mainWindow.ShowDialogOverlay();

            try
            {
                dialog.ShowDialog();
            }
            finally
            {
                mainWindow.HideDialogOverlay();
            }

            return;
        }

        dialog.ShowDialog();
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);

    private void ContinueButton_OnClick(object sender, RoutedEventArgs e)
        => ContinueRequested?.Invoke(this, EventArgs.Empty);
}
