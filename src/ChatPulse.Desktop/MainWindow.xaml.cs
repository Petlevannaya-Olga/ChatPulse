using System.ComponentModel;
using System.Windows;
using ChatPulse.Desktop.Views.Dialogs;

namespace ChatPulse.Desktop;

public partial class MainWindow : Window
{
    private bool _isCloseConfirmed;
    private bool _isOnboardingCompleted;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void WelcomeScreen_OnStartRequested(object sender, EventArgs e)
    {
        WelcomeScreen.Visibility = Visibility.Collapsed;
        FirstStepScreen.Visibility = Visibility.Visible;
        Title = "ChatPulse — Онбординг: шаг 1";
    }

    // Будущий последний шаг онбординга вызовет этот метод после успешного
    // сохранения профиля, авторизации Telegram и подключения хотя бы одного чата.
    public void MarkOnboardingCompleted()
    {
        _isOnboardingCompleted = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isCloseConfirmed || _isOnboardingCompleted)
        {
            base.OnClosing(e);
            return;
        }

        // Диалог появляется и на приветственном экране, и на первом шаге.
        // Закрытие крестиком, Alt+F4 и через панель задач обрабатывается одинаково.
        var dialog = new ExitOnboardingDialog { Owner = this };
        DialogOverlay.Visibility = Visibility.Visible;

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
            DialogOverlay.Visibility = Visibility.Collapsed;
        }

        base.OnClosing(e);
    }
}
