using System.Windows;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class TelegramLoginInProgressDialog : Window
{
    public TelegramLoginInProgressDialog()
    {
        InitializeComponent();
    }

    private void KeepLoginButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void CancelLoginButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

}
