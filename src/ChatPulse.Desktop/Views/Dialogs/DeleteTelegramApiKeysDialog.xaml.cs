using System.Windows;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class DeleteTelegramApiKeysDialog : Window
{
    public DeleteTelegramApiKeysDialog()
    {
        InitializeComponent();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Delete_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

}
