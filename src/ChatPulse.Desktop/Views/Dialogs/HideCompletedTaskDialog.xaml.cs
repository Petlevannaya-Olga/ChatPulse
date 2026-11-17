using System.Windows;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class HideCompletedTaskDialog : Window
{
    public HideCompletedTaskDialog()
    {
        InitializeComponent();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Hide_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
