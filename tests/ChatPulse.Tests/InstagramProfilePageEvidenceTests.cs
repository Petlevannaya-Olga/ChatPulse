using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramProfilePageEvidenceTests
{
    [Fact]
    public void AVisibleHeadingAndMatchingUrlConfirmTheExactProfile()
    {
        Assert.True(InstagramProfilePageEvidence.HasMatchingProfileHeading("realX",
            "https://www.instagram.com/realX/", ["realX"]));
        Assert.False(InstagramProfilePageEvidence.HasMatchingProfileHeading("realX",
            "https://www.instagram.com/real/", ["realX"]));
        Assert.False(InstagramProfilePageEvidence.HasMatchingProfileHeading("realX",
            "https://www.instagram.com/accounts/login/", ["realX"]));
        Assert.False(InstagramProfilePageEvidence.HasMatchingProfileHeading("realX",
            "https://www.instagram.com/realX/", ["other"]));
    }

    [Fact]
    public void OnlyExplicitUnavailableTextIsNegativeEvidence()
    {
        Assert.True(InstagramProfilePageEvidence.IsExplicitlyUnavailable(
            "Sorry, this page isn't available."));
        Assert.False(InstagramProfilePageEvidence.IsExplicitlyUnavailable("Please log in"));
        Assert.False(InstagramProfilePageEvidence.IsExplicitlyUnavailable("Try again later"));
    }
}
