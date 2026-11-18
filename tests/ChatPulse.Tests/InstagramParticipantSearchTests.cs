using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramParticipantSearchTests
{
    [Theory]
    [InlineData("https://www.instagram.com/Anna.Active/", "anna.active")]
    [InlineData("https://instagram.com/_member/?igsh=token", "_member")]
    public void UsesActivityInstagramProfile(string url, string expected)
        => Assert.Equal(expected, InstagramActivityProfile.TryGetUserName(url));

    [Theory]
    [InlineData("https://t.me/anna")]
    [InlineData("https://www.instagram.com/p/example/")]
    [InlineData("https://www.instagram.com/reel/example/")]
    [InlineData("https://www.instagram.com/anna/other/")]
    [InlineData("https://example.com/anna/")]
    public void RejectsNonProfileLinks(string url)
        => Assert.Null(InstagramActivityProfile.TryGetUserName(url));

    [Fact]
    public void SearchRequiresExactInstagramUserName()
    {
        var links = new[]
        {
            "https://www.instagram.com/member_extra/",
            "https://www.instagram.com/another/"
        };
        Assert.Equal(InstagramSearchStatus.INCONCLUSIVE,
            InstagramSearchEvidence.Classify("member", links, false));
        Assert.Equal(InstagramSearchStatus.NOT_FOUND,
            InstagramSearchEvidence.Classify("member", links, true));
        Assert.Equal(InstagramSearchStatus.FOUND,
            InstagramSearchEvidence.Classify("member", ["https://instagram.com/MEMBER/?igsh=foo"], true));
    }

    [Fact]
    public void FindsExactUsernameInVisibleResultRowWithoutHref()
    {
        Assert.Equal(InstagramSearchStatus.FOUND,
            InstagramSearchEvidence.Classify("mari.nook", [], false, ["Mari.Nook"]));
        Assert.Equal(InstagramSearchStatus.INCONCLUSIVE,
            InstagramSearchEvidence.Classify("mari.nook", [], false, ["mari.nook2"]));
        Assert.Equal(InstagramSearchStatus.INCONCLUSIVE,
            InstagramSearchEvidence.Classify("mari.nook", [], false));
    }

    [Theory]
    [InlineData("followers", "Подписчики", true)]
    [InlineData("followers", "Followers", true)]
    [InlineData("following", "Подписки", true)]
    [InlineData("following", "Ваши подписки", true)]
    [InlineData("following", "Following", true)]
    [InlineData("following", "Подписчики", false)]
    [InlineData("followers", "Подписки", false)]
    [InlineData("following", "Настройки", false)]
    public void RecognizesOnlyRequestedInstagramListDialog(string kind, string title, bool expected)
        => Assert.Equal(expected, InstagramParticipantDialogTitle.Matches(kind, [title]));

    [Theory]
    [InlineData("following", "Подписки", "Категории")]
    [InlineData("followers", "Подписчики", "Рекомендуемые")]
    public void DialogTitleCanBePlainTextEvenWhenUnrelatedHeadingsExist(
        string kind, string plainTextTitle, string unrelatedHeading)
    {
        // Instagram sometimes uses h2/h3 for inner sections, not the modal title.
        Assert.False(InstagramParticipantDialogTitle.Matches(kind, [unrelatedHeading]));
        Assert.True(InstagramParticipantDialogTitle.MatchesDialog(kind,
            [unrelatedHeading], [plainTextTitle, "Поиск"]));
        Assert.False(InstagramParticipantDialogTitle.MatchesDialog(kind,
            [unrelatedHeading], ["Поиск", "Рекомендации"]));
    }

    [Fact]
    public async Task ReadsOnlyCurrentInstagramParticipantActivityProfiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ChatPulseInstagram_{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ChatParticipants (
                        ParticipantId INTEGER PRIMARY KEY, TelegramUserId INTEGER, PeerType TEXT,
                        PeerId INTEGER, Position INTEGER, DisplayName TEXT,
                        SubscriptionProfileUrl TEXT, ActivityProfileUrl TEXT, IsCurrent INTEGER, Category TEXT);
                    INSERT INTO ChatParticipants VALUES
                      (1,10,'Channel',99,1,'Анна','https://www.instagram.com/subscription_anna/',
                       'https://www.instagram.com/active_anna/',1,'Обычный'),
                      (2,10,'Channel',99,2,'Мария','https://www.instagram.com/subscription_maria/',
                       'https://www.instagram.com/active_maria/',0,'Обычный'),
                      (3,10,'Channel',99,3,'Оля','https://www.instagram.com/subscription_olya/',
                       'https://t.me/not_an_instagram/',1,'Обычный');
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var chat = new ConfiguredChat
            {
                TelegramUserId = 10, PeerId = 99, PeerType = "Channel", TelegramTitle = "Instagram chat",
                SocialNetwork = "Instagram", SynchronizationState = ChatSynchronizationState.COMPLETED
            };
            var result = await new InstagramParticipantLookup(path).ReadCurrentAsync([chat]);
            var single = Assert.Single(result);
            Assert.Equal(1, single.ParticipantId);
            Assert.Equal("active_anna", single.UserName);
            Assert.Equal("https://www.instagram.com/active_anna/", single.ActivityProfileUrl);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("VIP", true)]
    [InlineData("SUPER VIP ⭐", true)]
    [InlineData("Основатель", true)]
    [InlineData("Директор", true)]
    [InlineData("АДМИНИСТРАТОР", true)]
    [InlineData("МОДЕРАТОР", true)]
    [InlineData("Обычный", false)]
    [InlineData("Выходные", false)]
    [InlineData("Штрафные дни", false)]
    public void BulkAuditExcludesOnlyVipAndAdministration(string category, bool expected)
        => Assert.Equal(expected, InstagramBatchEligibility.IsExcluded(category));

    [Fact]
    public void BulkPlanSeparatesSubscriptionAndActivityAndExcludesAdminsGlobally()
    {
        var current = new[]
        {
            new InstagramParticipantOption(1, "Ольга", "", "one.active", "Обычный",
                "https://instagram.com/one.subscribe/"),
            new InstagramParticipantOption(2, "Анна", "", "TWO.ACTIVE", "Обычный",
                "https://instagram.com/two.subscribe/"),
            new InstagramParticipantOption(3, "Анна второй чат", "", "two.active", "Обычный",
                "https://instagram.com/two.subscribe/"),
            new InstagramParticipantOption(4, "VIP", "", "vip.active", "SUPER VIP",
                "https://instagram.com/vip.subscribe/"),
            new InstagramParticipantOption(5, "Администратор", "", "admin.active", "Администратор",
                "https://instagram.com/admin.subscribe/"),
            new InstagramParticipantOption(6, "Администратор другого чата", "", "two.active", "Модератор",
                "https://instagram.com/two.subscribe/"),
            new InstagramParticipantOption(7, "Без ссылки", "", "", "Обычный"),
            new InstagramParticipantOption(8, "Выходной", "", "weekend.active", "Обычный",
                "https://instagram.com/weekend.subscribe/")
        };
        var result = InstagramBatchEligibility.Prepare(current);
        Assert.Equal(new[] { "one.active", "weekend.active" }, result.FollowsMeUserNames);
        Assert.Equal(new[] { "one.subscribe", "two.subscribe", "vip.subscribe", "admin.subscribe", "weekend.subscribe" },
            result.IFollowUserNames);
        Assert.Equal(7, result.SearchCount);
        Assert.Equal(3, result.ExcludedCount);
        Assert.Equal(1, result.MissingActivityProfileCount);
        Assert.Equal(1, result.MissingSubscriptionProfileCount);
    }

    [Fact]
    public void BulkPlanChecksOnlyMyFollowingForVipAndAdministration()
    {
        var members = new[]
        {
            new InstagramParticipantOption(1, "VIP", "", "vip.active", "SUPER VIP",
                "https://instagram.com/vip.subscribe/"),
            new InstagramParticipantOption(2, "Администратор", "", "admin.active", "Администратор",
                "https://instagram.com/admin.subscribe/"),
            new InstagramParticipantOption(3, "Обычная", "", "normal.active", "Обычный",
                "https://instagram.com/normal.subscribe/"),
            new InstagramParticipantOption(4, "Обычная из другого чата", "", "normal.active", "Обычный",
                "https://instagram.com/normal.subscribe/")
        };
        var selection = InstagramBatchEligibility.Prepare(members);

        Assert.Equal(new[] { "normal.active" }, selection.FollowsMeUserNames);
        Assert.Equal(new[] { "vip.subscribe", "admin.subscribe", "normal.subscribe" }, selection.IFollowUserNames);
        Assert.Equal(4, selection.SearchCount);
        Assert.Equal(1, selection.DuplicateCount);
    }

    [Fact]
    public void BulkPlanChecksFollowingEvenWhenAllParticipantsArePrivileged()
    {
        var selection = InstagramBatchEligibility.Prepare(
            [new InstagramParticipantOption(1, "VIP", "", "vip.active", "VIP",
                "https://instagram.com/vip.subscribe/")]);

        Assert.Empty(selection.FollowsMeUserNames);
        Assert.Equal(new[] { "vip.subscribe" }, selection.IFollowUserNames);
        Assert.Equal(1, selection.SearchCount);
    }

    [Fact]
    public void BulkResultsArePersistedDirectionByDirection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ChatPulseInstagramBatch_{Guid.NewGuid():N}.db");
        try
        {
            var store = new InstagramSubscriptionCheckStore(path);
            store.Save("my.main", "my.viewer", new InstagramBatchProgress("member.one", "followers",
                new InstagramSearchResult(InstagramSearchStatus.FOUND, "Найдена"), 1, 2));
            var initial = Assert.Single(store.Read("my.main", ["member.one"]));
            Assert.Equal("followers", initial.Kind);
            Assert.Equal("FOUND", initial.Status);
            store.Save("my.main", "my.viewer", new InstagramBatchProgress("member.one", "following",
                new InstagramSearchResult(InstagramSearchStatus.NOT_FOUND, "Нет в поиске"), 2, 2));
            var saved = store.Read("my.main", ["member.one"]);
            Assert.Equal(2, saved.Count);
            Assert.Contains(saved, item => item.Kind == "followers" && item.Status == "FOUND");
            Assert.Contains(saved, item => item.Kind == "following" && item.Status == "NOT_FOUND");
            Assert.Empty(store.Read("another.main", ["member.one"]));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

}
