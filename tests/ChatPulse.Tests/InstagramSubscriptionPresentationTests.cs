using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramSubscriptionPresentationTests
{
    [Fact]
    public void AllCurrentParticipantsIncludingVipAndAdministrationAreDisplayedExceptOwner()
    {
        var members = new[]
        {
            Make(1, "vip", "VIP"),
            Make(2, "admin", "Администратор"),
            Make(3, "normal", "Обычный"),
            Make(4, "my.main", "Обычный"),
            Make(5, "my.backup", "Обычный"),
            new InstagramParticipantOption(6, "Missing", "", "", "Обычный", "https://www.instagram.com/only.sub/", "Missing")
        };
        var own = InstagramOwnAccountFilter.GetUserNames(new InstagramOwnAccounts("my.main", "my.backup"));

        var rows = InstagramSubscriptionPresentation.Build(members, [], own);

        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, row => row.Category == "VIP");
        Assert.Contains(rows, row => row.Category == "Администратор");
        Assert.Contains(rows, row => row.ActivityUserName == "normal");
        Assert.Contains(rows, row => row.DisplayName == "Missing" && row.CanOpenProfile &&
            row.ProfileUserNameLabel == "@only.sub");
    }

    [Fact]
    public void SortsAdministrationThenVipThenRegularAndKeepsOriginalOrderWithinGroups()
    {
        var participants = new[]
        {
            Make(1, "regular.first", "Обычный"),
            Make(2, "vip.first", "VIP basic lite"),
            Make(3, "admin.first", "Модератор 14 чата"),
            Make(4, "regular.second", "Обычный"),
            Make(5, "admin.second", "Директор чатов"),
            Make(6, "vip.second", "ВИП"),
            Make(7, "admin.third", "Основатель"),
            Make(8, "admin.fourth", "Администратор")
        };

        var rows = InstagramSubscriptionPresentation.Build(participants, [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(new long[] { 3, 5, 7, 8, 2, 6, 1, 4 },
            rows.Select(row => row.ParticipantId));
        Assert.Equal(new InstagramParticipantCategoryCounts(4, 2, 2),
            InstagramParticipantCategoryCounts.FromRows(rows));
    }

    [Fact]
    public void FollowingUsesSubscriptionProfileAndFollowersUsesActivityProfile()
    {
        var participant = new InstagramParticipantOption(1, "Мария", "https://www.instagram.com/maria.active/",
            "maria.active", "Обычный", "https://www.instagram.com/maria.blog/", "Мария");
        var checks = new[]
        {
            new InstagramSavedCheck("maria.active", "followers", "FOUND", "2026-11-18T10:00:00Z"),
            new InstagramSavedCheck("maria.active", "following", "NOT_FOUND", "2026-11-18T10:02:00Z"),
            new InstagramSavedCheck("maria.blog", "following", "FOUND", "2026-11-18T10:01:00Z")
        };

        var row = Assert.Single(InstagramSubscriptionPresentation.Build([participant], checks,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        Assert.True(row.HasDifferentActivityProfile);
        Assert.Equal("Для подписки: @maria.blog", row.MainProfileLabel);
        Assert.Equal("Даёт актив с другой страницы: @maria.active", row.ActivityProfileLabel);
        Assert.Equal("Да", row.FollowsMeLabel);
        Assert.Equal("Да", row.IFollowLabel);
        Assert.False(row.HasDiscrepancy);
        Assert.Equal("https://www.instagram.com/maria.blog/", row.ProfileActionUrl);
        Assert.Equal("https://www.instagram.com/maria.active/", row.ActivityProfileUrl);
    }

    [Fact]
    public void ProfileColumnUsesSubscriptionLinkAndActivityAccountOnSecondLine()
    {
        var participant = new InstagramParticipantOption(1, "Мария", "https://www.instagram.com/maria.active/",
            "maria.active", "Обычный", "https://www.instagram.com/maria.blog/", "Мария");
        var checks = new[]
        {
            new InstagramSavedCheck("maria.blog", "following", "FOUND", "2026-11-18T10:01:00Z")
        };
        var row = Assert.Single(InstagramSubscriptionPresentation.Build([participant], checks,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        Assert.Equal("@maria.blog", row.ProfileUserNameLabel);
        Assert.Equal("https://www.instagram.com/maria.blog/", row.ProfileActionUrl);
        Assert.Equal(new Uri(row.ProfileActionUrl), row.ProfileActionUri);
        Assert.True(row.HasDifferentActivityProfile);
        Assert.Equal("@maria.active", row.ActivityUserNameLabel);
        Assert.Equal("https://www.instagram.com/maria.active/", row.ActivityProfileUrl);
        Assert.Equal(new Uri(row.ActivityProfileUrl), row.ActivityProfileUri);
        Assert.Matches(@"^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}$", row.LastCheck);
    }

    [Fact]
    public void SingleProfileUsesActivityAccountAndHasNoSecondaryLink()
    {
        var participant = Make(1, "single.profile", "Обычный");
        var row = Assert.Single(InstagramSubscriptionPresentation.Build([participant], [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        Assert.Equal("@single.profile", row.ProfileUserNameLabel);
        Assert.False(row.HasDifferentActivityProfile);
        Assert.Equal("—", row.LastCheck);
    }

    [Fact]
    public void MissingFollowingCheckForSubscriptionAccountIsUnverifiedEvenIfActivityWasChecked()
    {
        var participant = new InstagramParticipantOption(1, "Мария", "https://instagram.com/maria.active/",
            "maria.active", "Обычный", "https://instagram.com/maria.blog/", "Мария");
        var checks = new[]
        {
            new InstagramSavedCheck("maria.active", "following", "FOUND", "2026-11-18T10:00:00Z"),
            new InstagramSavedCheck("maria.active", "followers", "FOUND", "2026-11-18T10:00:00Z")
        };
        var row = Assert.Single(InstagramSubscriptionPresentation.Build([participant], checks,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        Assert.Equal("Да", row.FollowsMeLabel);
        Assert.Equal("Не проверено", row.IFollowLabel);
        Assert.True(row.IsUnverified);
    }

    [Fact]
    public void UncheckedAndInconclusiveResultsAreNeverPresentedAsConfirmedUnfollows()
    {
        var participant = Make(1, "active", "Основатель");
        var rows = InstagramSubscriptionPresentation.Build([participant],
            [new InstagramSavedCheck("active", "followers", "INCONCLUSIVE", "2026-11-18T10:00:00Z")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var row = Assert.Single(rows);
        Assert.Equal("—", row.FollowsMeLabel);
        Assert.Equal("Не проверено", row.IFollowLabel);
        Assert.True(row.IsUnverified);
        Assert.False(row.HasDiscrepancy);
    }

    [Theory]
    [InlineData("VIP")]
    [InlineData("Основатель")]
    [InlineData("Администратор")]
    [InlineData("Модератор")]
    public void PrivilegedParticipantHasDashEvenWithPreviouslySavedFollowersResult(string category)
    {
        var participant = Make(1, "privileged", category);
        var checks = new[]
        {
            new InstagramSavedCheck("privileged", "followers", "NOT_FOUND", "2026-11-17T10:00:00Z"),
            new InstagramSavedCheck("privileged", "following", "FOUND", "2026-11-18T10:00:00Z")
        };
        var row = Assert.Single(InstagramSubscriptionPresentation.Build([participant], checks,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        Assert.Equal("—", row.FollowsMeLabel);
        Assert.Equal("Да", row.IFollowLabel);
        Assert.False(row.IsUnverified);
        Assert.False(row.HasDiscrepancy);
    }

    [Fact]
    public void VipInAnotherChatSuppressesFollowersResultForSameActivityAccount()
    {
        var participant = Make(1, "shared.user", "Обычный");
        var checks = new[]
        {
            new InstagramSavedCheck("shared.user", "followers", "FOUND", "2026-11-18T10:00:00Z"),
            new InstagramSavedCheck("shared.user", "following", "NOT_FOUND", "2026-11-18T10:00:00Z")
        };
        var crossChatVip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shared.user" };
        var row = Assert.Single(InstagramSubscriptionPresentation.Build([participant], checks,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), crossChatVip));

        Assert.Equal("—", row.FollowsMeLabel);
        Assert.Equal("Нет", row.IFollowLabel);
        Assert.True(row.HasDiscrepancy);
    }

    private static InstagramParticipantOption Make(long id, string userName, string category) =>
        new(id, userName, $"https://www.instagram.com/{userName}/", userName, category,
            $"https://www.instagram.com/{userName}/", userName);
}
