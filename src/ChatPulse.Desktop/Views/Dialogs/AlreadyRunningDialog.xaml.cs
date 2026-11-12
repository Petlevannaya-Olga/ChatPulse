using System.Windows;

namespace ChatPulse.Desktop.Views.Dialogs;

/// <summary>
/// Startup-only notice: no DI host or main window exists yet when the instance lock fails.
/// </summary>
public partial class AlreadyRunningDialog : Window
{
    public AlreadyRunningDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
