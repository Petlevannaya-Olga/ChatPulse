using System.Windows;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class TelegramHelpDialog : Window
{
    public TelegramHelpDialog() => InitializeComponent();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
