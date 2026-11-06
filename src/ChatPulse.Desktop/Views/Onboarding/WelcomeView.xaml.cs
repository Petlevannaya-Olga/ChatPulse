using System.Windows;
using System.Windows.Controls;

namespace ChatPulse.Desktop.Views.Onboarding;

public partial class WelcomeView : UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
    }

    public event EventHandler? StartRequested;

    private void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        StartRequested?.Invoke(this, EventArgs.Empty);
    }
}
