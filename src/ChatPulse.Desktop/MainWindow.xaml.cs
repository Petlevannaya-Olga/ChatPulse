using System.Windows;

namespace ChatPulse.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void WelcomeScreen_OnStartRequested(object sender, EventArgs e)
    {
        WelcomeScreen.Visibility = Visibility.Collapsed;
        FirstStepScreen.Visibility = Visibility.Visible;
        Title = "ChatPulse — Онбординг: шаг 1";
    }
}
