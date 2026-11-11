using System.Diagnostics;
using System.Windows;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class TelegramApiKeysHelpDialog : Window
{
    public TelegramApiKeysHelpDialog()
    {
        InitializeComponent();
        ModalDialogKeyboardPolicy.Apply(this, DialogKeyboardFocusTarget);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OpenTelegramWebsite_OnClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://my.telegram.org")
        {
            UseShellExecute = true,
        });
    }
}
