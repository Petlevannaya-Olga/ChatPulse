using System.Windows;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class HideCompletedTaskDialog : Window
{
    public HideCompletedTaskDialog()
    {
        InitializeComponent();
    }

    public bool Accepted { get; private set; }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Hide_OnClick(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }
}
