using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramParticipantCategoryCountsTests
{
    [Fact]
    public void CountsCurrentChatInThreeExclusiveGroupsAndExcludesOwnedAccounts()
    {
        var participants = new[]
        {
            Make(1, "admin", "Администратор"),
            Make(2, "founder", "Основатель чатов ОРИ"),
            Make(3, "moderator", "Модератор"),
            Make(4, "director", "Директор"),
            Make(5, "vip", "VIP"),
            Make(6, "supervip", "SUPER VIP"),
            Make(7, "cyrillicvip", "ВИП"),
            Make(8, "regular", "Обычный"),
            Make(9, "vacation", "Выходной"),
            Make(10, "owner", "Обычный"),
            new InstagramParticipantOption(11, "Моя страница подписки", "https://www.instagram.com/another/",
                "another", "Обычный", "https://www.instagram.com/owner/")
        };
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "owner" };

        var counts = InstagramParticipantCategoryCounts.FromParticipants(participants, own);

        Assert.Equal(4, counts.Administration);
        Assert.Equal(3, counts.Vip);
        Assert.Equal(2, counts.Regular);
        Assert.Equal(9, counts.Total);
        var rows = InstagramSubscriptionPresentation.Build(participants, [], own);
        Assert.Equal(counts, InstagramParticipantCategoryCounts.FromRows(rows));
        Assert.Equal(rows.Count, counts.Total);
    }

    [Fact]
    public void FullRegisterCountsThirtyRegularEvenWhenOwnProfileIsExcludedFromChecking()
    {
        var participants = Enumerable.Range(1, 30).Select(number =>
            Make(number, number == 30 ? "own_profile" : $"regular_{number}", "Обычный")).ToArray();
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "own_profile" };

        Assert.Equal(30, InstagramParticipantCategoryCounts.FromRegister(participants).Regular);
        Assert.Equal(29, InstagramParticipantCategoryCounts.FromParticipants(participants, own).Regular);
        Assert.Equal(29, InstagramSubscriptionPresentation.Build(participants, [], own).Count);
    }

    [Fact]
    public void AdministrationTakesPriorityWhenCategoryIncludesVipAndAdministration()
    {
        var counts = InstagramParticipantCategoryCounts.FromParticipants(
            [Make(1, "adminvip", "VIP Администратор"), Make(2, "regular", "")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(new InstagramParticipantCategoryCounts(1, 0, 1), counts);
    }

    [Fact]
    public void EmptyListHasZeroCounts()
    {
        Assert.Equal(new InstagramParticipantCategoryCounts(0, 0, 0),
            InstagramParticipantCategoryCounts.FromParticipants([], new HashSet<string>()));
    }

    private static InstagramParticipantOption Make(long id, string userName, string category) =>
        new(id, userName, $"https://www.instagram.com/{userName}/", userName, category,
            $"https://www.instagram.com/{userName}/", userName);
}
