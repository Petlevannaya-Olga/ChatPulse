using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramListCollectionTests
{
    [Theory]
    [InlineData("123 followers", 123)]
    [InlineData("1,234 followers", 1234)]
    [InlineData("1 234 подписчика", 1234)]
    [InlineData("0 following", 0)]
    public void ParsesExactCounts(string label, int expected)
        => Assert.Equal(expected, InstagramListCollectionParser.ParseExactCount(label));

    [Theory]
    [InlineData("1.2K followers")]
    [InlineData("1,2 тыс. подписчиков")]
    [InlineData("unknown")]
    public void AbbreviatedCountsAreNotExact(string label)
        => Assert.Null(InstagramListCollectionParser.ParseExactCount(label));

    [Theory]
    [InlineData("https://www.instagram.com/olga/followers/", "olga", "followers", true)]
    [InlineData("https://instagram.com/OLGA/followers/?x=1", "olga", "followers", true)]
    [InlineData("https://www.instagram.com/olga/following/", "olga", "followers", false)]
    [InlineData("https://www.instagram.com/other/followers/", "olga", "followers", false)]
    [InlineData("https://other.example.com/olga/followers/", "olga", "followers", false)]
    public void MatchesListLinksIndependentOfQueryAndTrailingSlash(
        string url, string target, string kind, bool expected)
        => Assert.Equal(expected, InstagramListCollectionParser.IsListUrl(url, target, kind));

    [Fact]
    public void ParsesOnlyInstagramUserProfileLinks()
    {
        var names = InstagramListCollectionParser.ParseProfileLinks([
            "https://www.instagram.com/Anna.Profile/",
            "https://instagram.com/anna.profile/?igsh=foo",
            "https://www.instagram.com/_bob/",
            "https://www.instagram.com/p/example/",
            "https://example.org/not_an_instagram_profile/"
        ]);
        Assert.Equal(2, names.Count);
        Assert.Contains("anna.profile", names);
        Assert.Contains("_bob", names);
    }

    [Fact]
    public void OnlyMatchingExactCountCanConfirmCompleteness()
    {
        IReadOnlySet<string> users = new HashSet<string> { "a", "b" };
        Assert.False(new InstagramListCollection(users, 3, true).IsComplete);
        Assert.False(new InstagramListCollection(users, null, true).IsComplete);
        Assert.False(new InstagramListCollection(users, 2, false).IsComplete);
        Assert.True(new InstagramListCollection(users, 2, true).IsComplete);
    }
    [Fact]
    public void DiagnosticsDoNotImplyCompleteList()
    {
        IReadOnlySet<string> names = new HashSet<string> { "anna", "maria" };
        var result = new InstagramListCollection(names, 3, false,
            "Проход 1: 2; Проход 2: 2");

        Assert.False(result.IsComplete);
        Assert.Contains("Проход 2", result.Diagnostics);
        Assert.Equal(2, result.UserNames.Count);
    }
}
