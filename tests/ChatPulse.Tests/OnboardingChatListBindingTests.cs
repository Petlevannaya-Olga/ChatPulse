using System.ComponentModel;
using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class OnboardingChatListBindingTests
{
    [Fact]
    public void ChatSelectionScreen_ExposesLoadingAndStatusPropertiesForXaml()
    {
        // XAML uses the view itself as DataContext for its chat-list grid.
        // Losing this property silently breaks the spinner binding at runtime.
        var view = typeof(OnboardingStepThreeView);

        Assert.Equal(typeof(bool), view.GetProperty(nameof(OnboardingStepThreeView.IsLoadingChatList))?.PropertyType);
        Assert.Equal(typeof(string), view.GetProperty(nameof(OnboardingStepThreeView.ListStatus))?.PropertyType);
        Assert.Equal(typeof(bool), view.GetProperty(nameof(OnboardingStepThreeView.HasSelection))?.PropertyType);
        Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(view));
    }
}
