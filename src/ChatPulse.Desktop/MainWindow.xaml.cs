using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.ViewModels;
using ChatPulse.Desktop.Views.Dialogs;
using Microsoft.Extensions.Logging;

namespace ChatPulse.Desktop;

public partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly TelegramSetupViewModel _telegramSetup;
    private readonly ILogger<MainWindow> _logger;
    private TelegramOnboardingPage _telegramPage = TelegramOnboardingPage.ApiCredentials;
    private bool _isCloseConfirmed;
    private bool _isOnboardingCompleted;

    public MainWindow(
        TelegramSetupViewModel telegramSetup,
        ISettingsService settings,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _settings = settings;
        _telegramSetup = telegramSetup;
        _logger = logger;
        SecondStepScreen.DataContext = telegramSetup;
        SecondStepScreen.TelegramPageRequested += SecondStepScreen_OnTelegramPageRequested;
    }

    /// <summary>
    /// Вызывается до показа окна: восстанавливает имя и текущий экран из settings.json.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        try
        {
            await _telegramSetup.LoadApiCredentialsStatusAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Не падаем при испорченном защищённом файле; предлагаем повторную настройку.
            _logger.LogWarning("Не удалось прочитать локальные API-ключи ChatPulse");
            MessageBox.Show(this,
                "Не удалось прочитать сохранённые Telegram API-ключи. Проверьте учётную запись Windows.",
                "ChatPulse — Ключи Telegram", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // API-ключи и пользовательская сессия независимы. Восстанавливаем
        // подтверждённую авторизацию до выбора экрана онбординга.
        await _telegramSetup.RestoreSessionAsync(cancellationToken);

        _telegramPage = Enum.IsDefined(settings.CurrentTelegramPage)
            ? settings.CurrentTelegramPage : TelegramOnboardingPage.ApiCredentials;
        if (!_telegramSetup.IsApiConfigured && _telegramPage == TelegramOnboardingPage.Phone)
        {
            // Нельзя восстановить ввод номера, если API-ключи отсутствуют.
            _telegramPage = TelegramOnboardingPage.ApiCredentials;
            await _settings.SaveTelegramPageAsync(_telegramPage, cancellationToken);
        }

        _isOnboardingCompleted = settings.IsOnboardingCompleted;
        FirstStepScreen.RestoreName(settings.UserName);

        var step = settings.CurrentOnboardingStep;
        if (!Enum.IsDefined(step))
        {
            step = OnboardingStep.Welcome;
        }

        // Некорректные/старые настройки не должны пропускать обязательное имя.
        if (step is OnboardingStep.Telegram or OnboardingStep.FirstChat &&
            string.IsNullOrWhiteSpace(settings.UserName))
        {
            step = OnboardingStep.Profile;
        }

        // Не предлагаем заново войти в Telegram после успешного восстановления.
        // На шаге 3 пока остаётся заглушка для настройки первого чата.
        if (_telegramSetup.IsAuthorized)
        {
            _telegramPage = TelegramOnboardingPage.Phone;
            if (settings.CurrentTelegramPage != TelegramOnboardingPage.Phone)
                await _settings.SaveTelegramPageAsync(_telegramPage, cancellationToken);

            if (step == OnboardingStep.Telegram)
            {
                step = OnboardingStep.FirstChat;
                await _settings.SaveOnboardingStepAsync(step, cancellationToken);
            }
        }
        else if (step == OnboardingStep.FirstChat)
        {
            // Пока авторизация не проверена, не пропускаем обязательный шаг.
            step = OnboardingStep.Telegram;
            await _settings.SaveOnboardingStepAsync(step, cancellationToken);
        }

        // Главный экран будет реализован после настройки чатов.
        ShowStep(step);
    }

    private void ShowStep(OnboardingStep step)
    {
        WelcomeScreen.Visibility = step == OnboardingStep.Welcome
            ? Visibility.Visible : Visibility.Collapsed;
        FirstStepScreen.Visibility = step == OnboardingStep.Profile
            ? Visibility.Visible : Visibility.Collapsed;
        SecondStepScreen.Visibility = step == OnboardingStep.Telegram
            ? Visibility.Visible : Visibility.Collapsed;
        ThirdStepScreen.Visibility = step == OnboardingStep.FirstChat
            ? Visibility.Visible : Visibility.Collapsed;

        if (step == OnboardingStep.Telegram)
            SecondStepScreen.ShowTelegramPage(_telegramPage);

        Title = step switch
        {
            OnboardingStep.Profile => "ChatPulse — Онбординг: шаг 1",
            OnboardingStep.Telegram => "ChatPulse — Онбординг: шаг 2",
            OnboardingStep.FirstChat => "ChatPulse — Онбординг: шаг 3",
            _ => "ChatPulse — Пульс ваших чатов",
        };
    }

    private async Task NavigateAsync(OnboardingStep step)
    {
        try
        {
            // Сначала сохраняем переход, затем показываем экран: после перезапуска
            // пользователь окажется на том же шаге.
            await _settings.SaveOnboardingStepAsync(step);
            ShowStep(step);
        }
        catch (Exception exception) when (IsSettingsError(exception))
        {
            ShowSettingsError(exception);
        }
    }

    private async void WelcomeScreen_OnStartRequested(object sender, EventArgs e)
        => await NavigateAsync(OnboardingStep.Profile);

    private async void FirstStepScreen_OnBackRequested(object sender, EventArgs e)
        => await NavigateAsync(OnboardingStep.Welcome);

    private async void FirstStepScreen_OnContinueRequested(object sender, EventArgs e)
    {
        // View уже не вызывает этот переход при некорректном имени.
        // Не повторяем проверку в нескольких местах.
        try
        {
            await _settings.SaveUserNameAndStepAsync(
                FirstStepScreen.DisplayName, OnboardingStep.Telegram);
            // После перехода из «О себе» начинаем с API-ключей, даже если они сохранены:
            // тогда предлагаем продолжить без повторного ввода секрета.
            _telegramPage = TelegramOnboardingPage.ApiCredentials;
            ShowStep(OnboardingStep.Telegram);
        }
        catch (Exception exception) when (IsSettingsError(exception))
        {
            ShowSettingsError(exception);
        }
    }

    private async void SecondStepScreen_OnTelegramPageRequested(TelegramOnboardingPage page)
    {
        if (page == TelegramOnboardingPage.Phone && !_telegramSetup.IsApiConfigured)
            return;

        try
        {
            await _settings.SaveTelegramPageAsync(page);
            _telegramPage = page;
            SecondStepScreen.ShowTelegramPage(page);
        }
        catch (Exception exception) when (IsSettingsError(exception))
        {
            ShowSettingsError(exception);
        }
    }

    private async void SecondStepScreen_OnBackRequested(object sender, EventArgs e)
        => await NavigateAsync(OnboardingStep.Profile);

    private async void SecondStepScreen_OnContinueRequested(object sender, EventArgs e)
        => await NavigateAsync(OnboardingStep.FirstChat);

    private async void ThirdStepScreen_OnBackRequested(object sender, EventArgs e)
        => await NavigateAsync(OnboardingStep.Telegram);

    /// <summary>
    /// Вызывается будущим последним шагом только после успешного добавления чата.
    /// </summary>
    public async Task MarkOnboardingCompletedAsync(CancellationToken cancellationToken = default)
    {
        await _settings.CompleteOnboardingAsync(cancellationToken);
        _isOnboardingCompleted = true;
    }

    private static bool IsSettingsError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException;

    private void ShowSettingsError(Exception exception)
    {
        _logger.LogError(exception, "Не удалось сохранить настройки ChatPulse");
        MessageBox.Show(this,
            "Не удалось сохранить настройки. Проверьте доступ к папке ChatPulse в AppData.",
            "ChatPulse — Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void ShowDialogOverlay()
        => DialogOverlay.Visibility = Visibility.Visible;

    public void HideDialogOverlay()
        => DialogOverlay.Visibility = Visibility.Collapsed;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isCloseConfirmed || _isOnboardingCompleted)
        {
            base.OnClosing(e);
            return;
        }

        var dialog = new ExitOnboardingDialog { Owner = this };
        ShowDialogOverlay();

        try
        {
            if (dialog.ShowDialog() == true)
            {
                _isCloseConfirmed = true;
            }
            else
            {
                e.Cancel = true;
            }
        }
        finally
        {
            HideDialogOverlay();
        }

        base.OnClosing(e);
    }
}
