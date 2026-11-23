using System.IO;
using System.Xml.Linq;
using System.Xml.XPath;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramTaskVerificationTests
{
    [Theory]
    [InlineData("https://www.instagram.com/p/ABCde0123/?igsh=xyz", "https://www.instagram.com/p/ABCde0123/")]
    [InlineData("https://instagram.com/reel/Zz_12-3456/", "https://www.instagram.com/reel/Zz_12-3456/")]
    public void ExtractPostLink_StripsTrackingAndPreservesShortcode(string source, string expected)
    {
        Assert.True(InstagramTaskVerificationRules.TryGetPostUrl(source, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("https://www.instagram.com/example/", "example", true)]
    [InlineData("https://www.instagram.com/EXAMPLE/?hl=ru", "example", true)]
    [InlineData("https://www.instagram.com/example.fake/", "example", false)]
    [InlineData("https://www.instagram.com/p/example/", "example", false)]
    public void ProfileLinks_RequireExactUsername(string link, string user, bool expected)
        => Assert.Equal(expected, InstagramTaskVerificationRules.IsExactProfileLink(link, user));

    [Theory]
    [InlineData("Нравится")]
    [InlineData("Больше не нравится")]
    [InlineData("Like")]
    [InlineData("Unlike")]
    public void LikesCounterXPath_TargetsSiblingCountNotHeart(string heartLabel)
    {
        // Minimal DOM fixture matching the user's copied structure. The numeric
        // count changes without affecting the selector.
        var html = new XElement("article",
            new XElement("div",
                new XElement("span",
                    new XElement("div", new XAttribute("role", "button"),
                        new XElement("svg", new XAttribute("aria-label", heartLabel)))),
                new XElement("div", new XAttribute("role", "button"),
                    new XElement("span", "55"))),
            new XElement("div", new XAttribute("role", "button"),
                new XElement("span", "17")));
        var candidates = html.XPathSelectElements(
            InstagramTaskVerificationRules.LIKES_COUNTER_AFTER_HEART_XPATH).ToArray();

        var counter = Assert.Single(candidates);
        Assert.Equal("55", counter.Value);
        Assert.Empty(counter.Descendants("svg"));
    }

    [Theory]
    [InlineData("15 likes", true)]
    [InlineData("1,234 likes", true)]
    [InlineData("15 отметок Нравится", true)]
    [InlineData("Like", false)]
    [InlineData("Нравится", false)]
    [InlineData("15 comments", false)]
    [InlineData("", false)]
    public void LikesCounter_DoesNotMistakeHeartButtonForList(string label, bool expected)
        => Assert.Equal(expected, InstagramTaskVerificationRules.IsLikesCounterLabel(label));

    [Theory]
    [InlineData("Likes", true)]
    [InlineData("Нравится", true)]
    [InlineData("Отметки «Нравится»", true)]
    [InlineData("Comments", false)]
    [InlineData("Подписчики", false)]
    [InlineData("", false)]
    public void LikesDialogHeading_RejectsUnrelatedDialogs(string heading, bool expected)
        => Assert.Equal(expected, InstagramTaskVerificationRules.IsLikesDialogHeading(heading));

    [Fact]
    public void OnlyAllRequestedConfirmedChecksMayHide()
    {
        var confirmed = InstagramTaskCheckResult.Confirmed("найдено");
        var unknown = InstagramTaskCheckResult.Inconclusive("список неполный");
        const string URL = "https://www.instagram.com/p/ABCde0123/";
        Assert.True(InstagramTaskVerificationRules.Combine("tester", URL, confirmed, null).CanHide);
        Assert.True(InstagramTaskVerificationRules.Combine("tester", URL, null, confirmed).CanHide);
        Assert.True(InstagramTaskVerificationRules.Combine("tester", URL, confirmed, confirmed).CanHide);
        Assert.False(InstagramTaskVerificationRules.Combine("tester", URL, confirmed, unknown).CanHide);
        Assert.False(InstagramTaskVerificationRules.Combine("tester", URL, unknown, confirmed).CanHide);
        Assert.False(InstagramTaskVerificationRules.Combine("tester", URL, null, null).CanHide);
        Assert.Equal(10, InstagramTaskVerificationRules.MAX_LIKE_SCROLLS);
    }

    [Theory]
    [InlineData("Лайк, комментарий, сохранение и репост", true, true, true, true)]
    [InlineData("Поставьте лайк и сохраните пост", true, false, true, false)]
    [InlineData("Комментарий не нужен, поставьте лайк", true, false, false, false)]
    [InlineData("Без репоста, только сохранение", true, false, true, false)]
    [InlineData("Задание без указания реакций", true, false, false, false)]
    [InlineData("", true, false, false, false)]
    public void TaskTextSuggestsMandatoryLikeAndRequestedOtherActions(string text, bool like,
        bool comment, bool save, bool repost)
    {
        var selection = InstagramTaskVerificationRules.SuggestChecks(text);
        Assert.Equal((like, comment, save, repost), selection);
    }

    [Fact]
    public void FourActionVerificationRequiresEverySelectedConfirmation()
    {
        var yes = InstagramTaskCheckResult.Confirmed("подтверждено");
        var unknown = InstagramTaskCheckResult.Inconclusive("ошибка");
        const string URL = "https://www.instagram.com/p/ABCde0123/";
        var all = new InstagramTaskVerificationResult("tester", URL, yes, yes, yes, yes);
        Assert.True(all.CanHide);
        Assert.False((all with { Save = unknown }).CanHide);
        Assert.False((all with { Repost = unknown }).CanHide);
        Assert.True(new InstagramTaskVerificationResult("tester", URL, null, null, yes, null).CanHide);
    }

    [Fact]
    public async Task ConfirmedVerification_HidesOnlyCompletedMessageAndKeepsHistory()
    {
        using var temp = new TempDb();
        await SeedAsync(temp.File);
        var chat = Chat(101);
        var result = ConfirmedResult();
        var store = new WorkspaceTaskVerificationStore(temp.File);
        await store.PublishAsync(chat, 1, result);
        await store.PublishAsync(chat, 1, result);

        var tasks = new WorkspaceTaskReader(temp.File);
        Assert.Single(await tasks.GetTasksAsync(chat));
        Assert.Equal(2, (await tasks.GetTasksAsync(chat))[0].MessageId);
        Assert.Single(await tasks.GetTasksAsync(Chat(202)));

        await using var connection = new SqliteConnection($"Data Source={temp.File}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM ImportedChatMessages),
                (SELECT COUNT(*) FROM ImportedMessageVerified),
                (SELECT COUNT(*) FROM ImportedMessageHidden),
                (SELECT VerifiedLike FROM ImportedMessageVerified WHERE PeerId = 101 AND MessageId = 1)
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
    }

    [Fact]
    public async Task InconclusiveOrUncompleted_NeverCreatesHiddenOrVerifiedRows()
    {
        using var temp = new TempDb();
        await SeedAsync(temp.File);
        var store = new WorkspaceTaskVerificationStore(temp.File);
        var uncertain = InstagramTaskVerificationRules.Combine("tester",
            "https://www.instagram.com/p/ABCde0123/",
            InstagramTaskCheckResult.Inconclusive("10 прокруток"), null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync(Chat(101), 1, uncertain));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync(Chat(101), 2, ConfirmedResult()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync(Chat(101), 999, ConfirmedResult()));
        Assert.Equal(2, (await new WorkspaceTaskReader(temp.File).GetTasksAsync(Chat(101))).Count);
    }

    private static InstagramTaskVerificationResult ConfirmedResult() =>
        InstagramTaskVerificationRules.Combine("tester", "https://www.instagram.com/p/ABCde0123/",
            InstagramTaskCheckResult.Confirmed("найден"), null);

    private static ConfiguredChat Chat(long peer) => new()
    {
        TelegramUserId = 123, PeerId = peer, PeerType = "Channel",
        TelegramTitle = "Постчат", DisplayName = "Постчат", SocialNetwork = "Instagram", HistoryDays = 7
    };

    private static async Task SeedAsync(string db)
    {
        await using var connection = new SqliteConnection($"Data Source={db}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ImportedChatMessages (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL, Text TEXT,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            CREATE TABLE ImportedMessageReactionStates (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL, HasOwnReaction INTEGER,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
            );
            INSERT INTO ImportedChatMessages VALUES
                (123, 'Channel', 101, 1, 'https://www.instagram.com/p/ABCde0123/'),
                (123, 'Channel', 101, 2, 'https://www.instagram.com/p/XYZabc123/'),
                (123, 'Channel', 202, 1, 'https://www.instagram.com/p/ABCde0123/');
            INSERT INTO ImportedMessageReactionStates VALUES
                (123, 'Channel', 101, 1, 1),
                (123, 'Channel', 202, 1, 1);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TempDb : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
            $"ChatPulse-TaskVerify-{Guid.NewGuid():N}");
        public string File => Path.Combine(DirectoryPath, "history.db");
        public TempDb() => Directory.CreateDirectory(DirectoryPath);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(DirectoryPath);
    }
}
