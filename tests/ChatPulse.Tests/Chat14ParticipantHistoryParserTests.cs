using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class Chat14ParticipantHistoryParserTests
{
    [Fact]
    public void RecognizesBrokenFullListAsCandidateInsteadOfSilentlySkippingIt()
    {
        var parser = new Chat14ParticipantHistoryParser();
        Assert.True(parser.IsCandidate("VIP УЧАСТНИКИ\nВСЕ МЫ\nссылка повреждена"));
    }

    private static readonly DateTime POST_DATE =
        new(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<string> EMPTY_KNOWN =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private const string LIST = """
        ***14 ЧАТ***
        ⚡️Основатель чатов ОРИОН
        Виктория
        https://www.instagram.com/danilovavica
        ⚡️Модератор 14 чата
        Николь
        https://www.instagram.com/poznavaysebya
        🌟VIP УЧАСТНИКИ 🌟
        1 VIP Plus lite ⭐️
        https://www.instagram.com/stolyarova_travel
        2 VIP basic lite ⭐️
        https://instagram.com/_vlks__
        ⚡️🦋 ВСЕ МЫ 🦋 ⚡️
        1
        https://www.instagram.com/marinaburnt
        Подписка сюда 👆🏼
        Актив дает отсюда 👇🏼
        https://www.instagram.com/marinamarina8048
        2
        https://instagram.com/_mar_g.o
        Подписка сюда 👆🏼
        Актив дает отсюда 👇🏼
        https://instagram.com/mar_fozzz
        Выходные с 22 по 29 октября
        3
        https://www.instagram.com/another_user
        4
        https://instagram.com/_mar_g.o
        Подписка сюда 👆🏼
        Актив дает отсюда 👇🏼
        https://instagram.com/mar_fozzz
        💃🏻ВЫХОДНЫЕ💃🏻
        5
        https://www.instagram.com/another_user
        ❌ШТРАФНЫЕ ДНИ❌
        6
        https://www.com/mariagorbunovarus/
        7
        https://instagram.com/_mar_g.oХ/
        """;

    [Fact]
    public async Task ParsesAllSections_Duplicates_BrokenPenaltyDomain_AndIndependentStatusFlags()
    {
        var parser = new Chat14ParticipantHistoryParser();
        Assert.True(parser.IsCandidate(LIST));
        var result = await parser.ParseAsync(LIST, POST_DATE, EMPTY_KNOWN, null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(11, result.Entries!.Count);
        var duplicate = result.Entries.Where(item =>
            item.SubscriptionProfileUrl.EndsWith("/_mar_g.o", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, duplicate.Length);
        Assert.Equal(2, duplicate.Count(item => item.SourceSection == "ВСЕ МЫ"));
        Assert.All(duplicate.Where(item => item.SourceSection == "ВСЕ МЫ"), item =>
            Assert.Equal("https://www.instagram.com/mar_fozzz", item.ActivityProfileUrl));
        Assert.Contains(result.Entries, item => item.IsPenalty &&
            item.SubscriptionProfileUrl == "https://www.instagram.com/mariagorbunovarus");
        Assert.Contains(result.Entries, item => item.IsPenalty &&
            item.SubscriptionProfileUrl == "https://www.instagram.com/_mar_g.o");
        Assert.Contains(result.Entries, item => item.IsWeekend &&
            item.SubscriptionProfileUrl == "https://www.instagram.com/another_user");
        var weekend = result.Entries.Single(item => item.WeekendFromDate.HasValue);
        Assert.Equal(new DateOnly(2026, 10, 22), weekend.WeekendFromDate);
        Assert.Equal(new DateOnly(2026, 10, 29), weekend.WeekendToDate);
    }

    [Fact]
    public async Task RejectsDamagedNonPenaltyLinkWithSectionAndRecordNumber()
    {
        var text = LIST.Replace("https://www.instagram.com/another_user",
            "https://www.com/another_user", StringComparison.Ordinal);
        var result = await new Chat14ParticipantHistoryParser().ParseAsync(text, POST_DATE,
            EMPTY_KNOWN, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("ВСЕ МЫ", result.Error, StringComparison.Ordinal);
        Assert.Contains("www.com", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvesLatinXSuffixOnlyFromKnownIdentityOrConclusiveEvidence()
    {
        const string suffix = "https://www.com/masha_activex/";
        var text = LIST.Replace("https://www.com/mariagorbunovarus/", suffix,
            StringComparison.Ordinal);
        var parser = new Chat14ParticipantHistoryParser();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "masha_active" };
        var alreadyKnown = await parser.ParseAsync(text, POST_DATE, known, null,
            CancellationToken.None);
        Assert.True(alreadyKnown.IsSuccess, alreadyKnown.Error);
        Assert.Contains(alreadyKnown.Entries!, item => item.IsPenalty &&
            item.SubscriptionProfileUrl == "https://www.instagram.com/masha_active");

        var withoutBrowser = await parser.ParseAsync(text, POST_DATE, EMPTY_KNOWN, null,
            CancellationToken.None);
        Assert.False(withoutBrowser.IsSuccess);
        Assert.Contains("латинской X", withoutBrowser.Error, StringComparison.Ordinal);

        var checkedUrls = new List<string>();
        var withBrowser = await parser.ParseAsync(text, POST_DATE, EMPTY_KNOWN,
            (url, _) =>
            {
                checkedUrls.Add(url);
                return Task.FromResult<bool?>(url.EndsWith("/masha_active", StringComparison.Ordinal));
            }, CancellationToken.None);
        Assert.True(withBrowser.IsSuccess, withBrowser.Error);
        Assert.Equal(2, checkedUrls.Count);
        Assert.Contains(withBrowser.Entries!, item => item.SubscriptionProfileUrl ==
            "https://www.instagram.com/masha_active" && item.IsPenalty);

        var inconclusive = await parser.ParseAsync(text, POST_DATE, EMPTY_KNOWN,
            (_, _) => Task.FromResult<bool?>(null), CancellationToken.None);
        Assert.False(inconclusive.IsSuccess);
        Assert.Contains("неоднозначна", inconclusive.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AmbiguousBothExistingProfilesCannotBeGuessed()
    {
        var text = LIST.Replace("https://www.com/mariagorbunovarus/",
            "www.com/masha_activex", StringComparison.Ordinal);
        var parser = new Chat14ParticipantHistoryParser();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "masha_activex", "masha_active" };
        var result = await parser.ParseAsync(text, POST_DATE, known,
            (_, _) => Task.FromResult<bool?>(true), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("неоднозначна", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsIncompleteListWithoutCreatingFalseDepartures()
    {
        var text = LIST.Replace("https://www.instagram.com/another_user",
            string.Empty, StringComparison.Ordinal);
        var result = await new Chat14ParticipantHistoryParser().ParseAsync(text,
            POST_DATE, EMPTY_KNOWN, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("отсутствует ссылка", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsConflictingDuplicatePrimaryProfilesBeforePublishingIdentities()
    {
        const string originalActivity = "https://instagram.com/mar_fozzz";
        var index = LIST.LastIndexOf(originalActivity, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var text = LIST[..index] + "https://instagram.com/conflicting_activity" +
                   LIST[(index + originalActivity.Length)..];
        var result = await new Chat14ParticipantHistoryParser().ParseAsync(text,
            POST_DATE, EMPTY_KNOWN, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Противоречащие повторные", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDoesNotReturnPartiallyParsedList()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Chat14ParticipantHistoryParser().ParseAsync(LIST, POST_DATE,
                EMPTY_KNOWN, null, cancellation.Token));
    }
}
