using ChatPulse.Desktop.ViewModels;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>Step 1: validation, avatar and bindable state (no WPF window required).</summary>
public sealed class OnboardingProfileCoverageTests
{
    [Fact]
    public void NewProfile_HasNoValidNameAndUsesPlaceholderAvatar()
    {
        var profile = new ProfileSetupViewModel();

        Assert.Equal(string.Empty, profile.Name);
        Assert.True(profile.IsNameEmpty);
        Assert.False(profile.IsNameInvalid);
        Assert.False(profile.IsNameValid);
        Assert.Equal("?", profile.AvatarInitial);
        Assert.Empty(profile.ValidationMessage);
    }

    [Theory]
    [InlineData("А")]
    [InlineData(" A ")]
    [InlineData("Ж")]
    [InlineData("x")]
    [InlineData(" a")]
    [InlineData("a ")]
    [InlineData("a" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void InvalidNonEmptyNames_ShowValidationError(string value)
    {
        var profile = new ProfileSetupViewModel { Name = value };

        Assert.False(profile.IsNameValid);
        Assert.True(profile.IsNameInvalid);
        Assert.False(profile.IsNameEmpty);
        Assert.Contains("от 2 до 50", profile.ValidationMessage);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t\n")]
    [InlineData("")]
    public void EmptyOrWhitespaceNames_DoNotShowErrorUntilUserHasEnteredText(string value)
    {
        var profile = new ProfileSetupViewModel { Name = value };

        Assert.True(profile.IsNameEmpty);
        Assert.False(profile.IsNameInvalid);
        Assert.Empty(profile.ValidationMessage);
        Assert.Equal("?", profile.AvatarInitial);
    }

    [Theory]
    [InlineData("AB")]
    [InlineData(" Ольга ")]
    [InlineData("Мария Анна")]
    public void ValidNames_AllowContinuation(string value)
    {
        var profile = new ProfileSetupViewModel { Name = value };

        Assert.True(profile.IsNameValid);
        Assert.False(profile.IsNameInvalid);
        Assert.False(profile.IsNameEmpty);
        Assert.Empty(profile.ValidationMessage);
    }

    [Fact]
    public void NamesWithFiftyTrimmedCharacters_AreValid()
    {
        var profile = new ProfileSetupViewModel { Name = " " + new string('А', 50) + " " };

        Assert.True(profile.IsNameValid);
        profile.Name = new string('А', 51);
        Assert.False(profile.IsNameValid);
    }

    [Theory]
    [InlineData(" ольга ", "О")]
    [InlineData(" marina", "M")]
    [InlineData("ёж", "Ё")]
    public void Avatar_UsesFirstNonWhitespaceCharacterInUppercase(string name, string initial)
    {
        var profile = new ProfileSetupViewModel { Name = name };
        Assert.Equal(initial, profile.AvatarInitial);
    }

    [Fact]
    public void ChangingName_RaisesAllDependentPropertiesExactlyOnce()
    {
        var profile = new ProfileSetupViewModel();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.Name = "Ольга";

        Assert.Equal(1, changed.Count(name => name == nameof(profile.Name)));
        Assert.Contains(nameof(profile.IsNameValid), changed);
        Assert.Contains(nameof(profile.IsNameInvalid), changed);
        Assert.Contains(nameof(profile.IsNameEmpty), changed);
        Assert.Contains(nameof(profile.AvatarInitial), changed);
        Assert.Contains(nameof(profile.ValidationMessage), changed);
    }

    [Fact]
    public void ReassigningSameName_DoesNotProduceSpuriousNotifications()
    {
        var profile = new ProfileSetupViewModel { Name = "Ольга" };
        var notifications = 0;
        profile.PropertyChanged += (_, _) => notifications++;

        profile.Name = "Ольга";

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void CorrectingAnInvalidName_ImmediatelyClearsValidationError()
    {
        var profile = new ProfileSetupViewModel { Name = "О" };
        Assert.True(profile.IsNameInvalid);

        profile.Name = "Ольга";

        Assert.True(profile.IsNameValid);
        Assert.Empty(profile.ValidationMessage);
    }
}
