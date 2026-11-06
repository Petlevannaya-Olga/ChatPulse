using System.Windows;
using System.Windows.Controls;

namespace ChatPulse.Desktop.Views.Onboarding;

public partial class OnboardingStepThreeView : UserControl
{
    public OnboardingStepThreeView() => InitializeComponent();
    public event EventHandler? BackRequested;
    private void BackButton_OnClick(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);
}
