using System.Windows;
using System.Windows.Input;

namespace ChatPulse.Desktop.Views.Dialogs;

public enum TelegramSessionAction
{
    ResetSession,
    ChangeApiKeys
}

public partial class TelegramSessionActionDialog : Window
{
    public TelegramSessionActionDialog(TelegramSessionAction action)
    {
        InitializeComponent();
        ModalDialogKeyboardPolicy.Apply(this, DialogKeyboardFocusTarget);

        if (action == TelegramSessionAction.ChangeApiKeys)
        {
            DialogHeading.Text = "Сохранить новые API-ключи?";
            DialogSubtitle.Text = "Сохранённые ключи будут заменены";
            DialogDescription.Text =
                "ChatPulse заменит ранее сохранённые API-ключи и сбросит локальную Telegram-сессию, " +
                "если она существует. Для повторного подключения аккаунта потребуется авторизация. " +
                "Продолжить?";
            ConfirmButton.Content = "Сохранить ключи";
            ConfirmButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(126, 65, 220));
        }
        else
        {
            DialogHeading.Text = "Сбросить Telegram-сессию?";
            DialogSubtitle.Text = "API-ключи останутся сохранёнными";
            DialogDescription.Text =
                "ChatPulse удалит только локальную Telegram-сессию. При следующем входе " +
                "потребуется снова ввести номер телефона и код подтверждения. " +
                "API ID и API Hash останутся на этом компьютере.";
            ConfirmButton.Content = "Сбросить сессию";
        }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Confirm_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}
