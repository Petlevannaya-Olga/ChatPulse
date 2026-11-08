using System.Windows;
using System.Windows.Controls;
using ChatPulse.Desktop.ViewModels;

namespace ChatPulse.Desktop.Views.Onboarding;

public partial class OnboardingStepThreeView : UserControl
{
    public OnboardingStepThreeView() => InitializeComponent();

    public event EventHandler? BackRequested;

    private FirstChatSetupViewModel? ViewModel => DataContext as FirstChatSetupViewModel;

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && ViewModel is { } viewModel)
            await viewModel.EnsureLoadedAsync();
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
            await viewModel.RefreshAsync();
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
            await viewModel.SaveAsync();
    }
}
