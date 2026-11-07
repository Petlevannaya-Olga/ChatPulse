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

        if (action == TelegramSessionAction.ChangeApiKeys)
        {
            DialogHeading.Text = "Сохранить новые API-ключи?";
            DialogSubtitle.Text = "Потребуется повторный вход в Telegram";
            DialogDescription.Text =
                "При сохранении новых API-ключей ChatPulse удалит текущую локальную Telegram-сессию. " +
                "После этого нужно будет заново авторизовать аккаунт. Старые ключи будут заменены.";
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
