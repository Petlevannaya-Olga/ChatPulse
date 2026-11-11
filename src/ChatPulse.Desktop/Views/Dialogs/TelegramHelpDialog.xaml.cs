using System.Windows;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class TelegramHelpDialog : Window
{
    public TelegramHelpDialog()
    {
        InitializeComponent();
        ModalDialogKeyboardPolicy.Apply(this, DialogKeyboardFocusTarget);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
