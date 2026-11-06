using System.Windows;
using System.Windows.Controls;

namespace ChatPulse.Desktop.Views.Onboarding;

public partial class OnboardingStepTwoView : UserControl
{
    public OnboardingStepTwoView()
    {
        InitializeComponent();
    }

    public event EventHandler? BackRequested;

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e)
    {
        Window.GetWindow(this)?.Close();
    }
}
