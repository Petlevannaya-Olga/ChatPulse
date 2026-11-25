using System.IO;
using System.Text;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using Microsoft.Data.Sqlite;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class Chat16TelegramParticipantHistoryParserTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private const string Header = """
        ⚡️⚡️⚡️⚡⚡️ ⚡️
        ⚡️ОСНОВАТЕЛЬ КОМПАНИИ⚡️
        Виктория / Lifestyle
        💘ДИРЕКТОР 9 ВЕКТОРА💘
        Татьяна
        @tati_tatipo
        💘АДМИНИСТРАТОР💘
        2 ЛИНЕЙКИ
        Виктория
        https://t.me/VOA_mir
        💘МОДЕРАТОР ЧАТА💘
        2 ЛИНЕЙКИ
        Акана
        https://t.me/airfeya
        ⚡️ ⚡️⚡️⚡️ ⚡️
        """;

    private static readonly string[] Profiles =
    [
        "BrainStain_with_Polina", "alinafotografff", "dishka_love_13", "taro_with_anna",
        "pnkaaKa", "+57AnhBiD3zI4ZTQy", "soul_light_workshop", "mksveji",
        "ish_crystal_bags", "AureliaDN", "e_lkina", "+aRtfNBX9o4NhOTA6",
        "superklassnaya1", "gguoahgdd", "psyyulkins", "+F9KYYdNSn9VjYmMy",
        "Dasha_koshe", "litd_vv", "+d_yADztfOMlhMzky", "+kNw6LlGPBOhiNDFi",
        "+5dAmKJPq7Y83MDFi", "Alina_defectolog", "Vellary_and_Vika"
    ];

    private static string Section(IEnumerable<string> profiles, int start = 1)
    {
        var text = new StringBuilder();
        foreach (var (url, index) in profiles.Select((profile, index) => (profile, index + start)))
            text.Append(index).Append('\n').Append("https://t.me/").Append(url)
                .Append('\n').Append("Участник ").Append(index).Append(" / Lifestyle\n");
        return text.ToString();
    }

    private static Task<ChatParticipantHistoryParseResult> Parse(string text) =>
        new Chat16TelegramParticipantHistoryParser().ParseAsync(text,
            new DateTime(2026, 11, 25, 17, 0, 0, DateTimeKind.Utc), Known,
            null, CancellationToken.None);

    [Fact]
    public async Task CompleteTwentyOneMemberListRetainsPenaltyWithinMembership()
    {
        var text = Header + "\n" + Section(Profiles.Take(20)) +
                   "❌ ШТРАФНОЙ ДЕНЬ ❌\n21\nhttps://t.me/nejnost_2022\nАлёна / Студия лазер";
        var parsed = await Parse(text);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(24, parsed.Entries!.Count); // 21 numbered + 3 identifiable administrators
        Assert.Equal(21, parsed.Entries.Count(item => item.Position.HasValue));
        var penalty = Assert.Single(parsed.Entries.Where(item => item.IsPenalty));
        Assert.Equal(21, penalty.Position);
        Assert.Equal("https://t.me/nejnost_2022", penalty.SubscriptionProfileUrl);
    }

    [Fact]
    public async Task RepeatedIncompletePrefixUsesFinalCompleteCopy()
    {
        var prefix = Header + "\n" + Section(Profiles.Take(20)) + "\n21\n";
        var full = Header + "\n" + Section(Profiles) +
                   "❌ ШТРАФНОЙ ДЕНЬ ❌\n24\nhttps://t.me/nejnost_2022\nАлёна / Студия лазер";
        var parser = new Chat16TelegramParticipantHistoryParser();
        Assert.True(parser.IsCandidate(prefix + full));
        var parsed = await Parse(prefix + full);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(27, parsed.Entries!.Count);
        Assert.Equal(24, parsed.Entries.Count(item => item.Position.HasValue));
        Assert.Equal(24, Assert.Single(parsed.Entries.Where(item => item.IsPenalty)).Position);
    }

    [Fact]
    public async Task OlderListHasNoPenaltyAndDifferentMembership()
    {
        var old = Profiles.Take(14).Append("nejnost_2022").Append("psyyulkins")
            .Append("angel77777i").Concat(Profiles.Skip(15).Take(5)).ToArray();
        var parsed = await Parse(Header + "\n" + Section(old));
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(22, parsed.Entries!.Count(item => item.Position.HasValue));
        Assert.DoesNotContain(parsed.Entries, item => item.IsPenalty);
        Assert.Contains(parsed.Entries, item => item.SubscriptionProfileUrl ==
            "https://t.me/angel77777i");
    }

    [Fact]
    public async Task ContradictoryCopiesAreRejected()
    {
        var first = Header + "\n1\nhttps://t.me/first_person\nПервая / Lifestyle\n";
        var second = Header + "\n1\nhttps://t.me/other_person\nВторая / Lifestyle\n";
        var parsed = await Parse(first + second);
        Assert.False(parsed.IsSuccess);
        Assert.Contains("противоречат", parsed.Error);
    }

    [Fact]
    public async Task MissingLinkInCompleteListRejectsEntireList()
    {
        var parsed = await Parse(Header + "\n1\nНет ссылки\nПолина / Lifestyle");
        Assert.False(parsed.IsSuccess);
        Assert.Contains("№1", parsed.Error);
    }

    [Fact]
    public void TelegramLinksAreAcceptedWithoutMisidentifyingInviteTokens()
    {
        Assert.True(ChatParticipantHistoryStore.TryNormalizeProfile(
            "https://t.me/+AaBbCcDd12345", out var a, out var url));
        Assert.True(ChatParticipantHistoryStore.TryNormalizeProfile(
            "https://t.me/+aabbccdd12345", out var b, out _));
        Assert.NotEqual(a, b);
        Assert.Equal("https://t.me/+AaBbCcDd12345", url);
        Assert.True(ChatParticipantHistoryStore.TryNormalizeProfile(
            "https://t.me/BrainStain_with_Polina", out var username, out _));
        Assert.Equal("tg:brainstain_with_polina", username);
    }

    [Fact]
    public async Task FullHistoryAndDailySyncUseTheSameTelegramIdentityAndKeepLiveCursorIndependent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulseTg16_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "test.db");
            var chat = new ConfiguredChat
            {
                TelegramUserId = 123, PeerId = 16, PeerType = "Channel",
                TelegramTitle = "16 Пост чат тк", SocialNetwork = "Telegram",
                SynchronizationState = ChatSynchronizationState.COMPLETED
            };
            var old = Header + "\n" + Section(Profiles.Take(20).Append("nejnost_2022"));
            var middle = Header + "\n" + Section(Profiles.Take(20)) +
                "\n❌ ШТРАФНОЙ ДЕНЬ ❌\n21\nhttps://t.me/nejnost_2022\nАлёна / Студия лазер";
            var newest = Header + "\n" + Section(Profiles) +
                "\n❌ ШТРАФНОЙ ДЕНЬ ❌\n24\nhttps://t.me/nejnost_2022\nАлёна / Студия лазер";
            var parser = new Chat16TelegramParticipantHistoryParser();
            var candidates = new List<ParticipantHistoryCandidate>();
            foreach (var (id, body) in new (long Id, string Body)[]
                     { (10, old), (20, middle), (30, newest) })
            {
                var parsed = await Parse(body);
                Assert.True(parsed.IsSuccess, parsed.Error);
                candidates.Add(new ParticipantHistoryCandidate(id, null, body, parsed.Entries));
            }
            var history = new ChatParticipantHistoryStore(database);
            var rebuilt = await history.RebuildAsync(chat, candidates, parser.Rule);
            Assert.Equal("COMPLETED", rebuilt.Outcome);
            var changes = await history.ReadChangesAsync(chat);
            var alena = changes.Where(item => item.InstagramUsername == "tg:nejnost_2022")
                .OrderBy(item => item.MessageId).ToArray();
            Assert.Equal(new long[] { 10, 20 }, alena.Select(item => item.MessageId));
            Assert.False(alena[0].IsPenalty);
            Assert.True(alena[1].IsPenalty);
            Assert.Null(await new ChatParticipantRegistry(database).ReadLatestSourceAsync(chat));
            // After full history restore, the live cursor is still empty.
            // A daily batch can publish the newest snapshot without duplicating history.
            await new ChatParticipantDailySyncStore(database).CommitAsync(chat, candidates,
                parser.Rule, expectedLiveMessageId: null,
                outcomeDescription: "Три списка обработаны");
            Assert.Equal(30L, (await new ChatParticipantRegistry(database)
                .ReadLatestSourceAsync(chat))?.MessageId);
            Assert.Equal(3, (await history.ReadSnapshotsAsync(chat)).Count);
            var live = await new TelegramParticipantReadStore(database).ReadCurrentAsync(chat);
            Assert.Equal(27, live.Count); // 24 numbered + 3 identifiable administrators
            Assert.Equal("Штрафной день", live.Single(item =>
                item.ProfileUrl.EndsWith("/nejnost_2022", StringComparison.Ordinal)).StatusLabel);
        }
        finally
        {
            SqliteTestDatabaseCleanup.DeleteDirectory(directory);
        }
    }


    [Fact]
    public async Task MultipleDifferentAdministratorsAreNotRejectedAsDuplicateRole()
    {
        var additionalAdministrator = """
            💘АДМИНИСТРАТОР💘
            Марина
            https://t.me/second_admin
            """;
        var text = Header + "\n" + additionalAdministrator + "\n" + Section(Profiles.Take(2));
        var parsed = await Parse(text);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(2, parsed.Entries!.Count(item => item.Category == "Администратор"));
        Assert.Equal(6, parsed.Entries.Count); // director, 2 admins, moderator, 2 members
    }

    [Fact]
    public async Task RepeatedSameAdministratorWithSameTelegramProfileIsOnePerson()
    {
        var duplicate = """
            💘АДМИНИСТРАТОР💘
            Виктория
            https://t.me/VOA_mir
            """;
        var parsed = await Parse(Header + "\n" + duplicate + "\n" + Section(Profiles.Take(1)));
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Single(parsed.Entries!.Where(item => item.Category == "Администратор"));
    }

    [Fact]
    public async Task RestartedNumberingWithIdenticalPrefixUsesLastCompleteList()
    {
        var earlier = Section(Profiles.Take(10));
        var final = Section(Profiles.Take(13));
        var parsed = await Parse(Header + "\n" + earlier + final);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(13, parsed.Entries!.Count(item => item.Position.HasValue));
    }

    [Fact]
    public async Task RestartedNumberingWithContradictoryEntryIsStillRejected()
    {
        var first = Section(Profiles.Take(10));
        var changed = new[] { "different_identity" }.Concat(Profiles.Skip(1).Take(12));
        var parsed = await Parse(Header + "\n" + first + Section(changed));
        Assert.False(parsed.IsSuccess);
        Assert.Contains("не совпадают", parsed.Error);
    }

    [Fact]
    public async Task SingleWordNameWithoutSlashIsAccepted()
    {
        var parsed = await Parse(Header + "\n1\nhttps://t.me/name_only\nПолина");
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal("Полина", Assert.Single(parsed.Entries!.Where(item => item.Position.HasValue)).DisplayName);
    }

    [Fact]
    public async Task TrulyMissingTelegramLinkRemainsAnError()
    {
        var parsed = await Parse(Header + "\n1\nПолина / Таро");
        Assert.False(parsed.IsSuccess);
        Assert.Contains("Telegram-ссылка", parsed.Error);
    }

    [Fact]
    public async Task ReviewedGelyaShortUrlIsResolvedWithOriginalUrlAndProvenance()
    {
        var parsed = await Parse(Header + "\n1\nhttps://t.me/e_\nГеля / Lifestyle");
        Assert.True(parsed.IsSuccess, parsed.Error);
        var row = Assert.Single(parsed.Entries!.Where(item => item.Position.HasValue));
        Assert.Equal("https://t.me/e_lkina", row.SubscriptionProfileUrl);
        Assert.Equal("https://t.me/e_", row.SourceProfileUrl);
        Assert.Contains("e_lkina", row.ResolutionNote);
        Assert.Equal("Геля / Lifestyle", row.DisplayName);
    }

    [Fact]
    public async Task ShortUrlForUnrelatedPersonIsNotAutoRepaired()
    {
        var parsed = await Parse(Header + "\n1\nhttps://t.me/e_\nДругая / Lifestyle");
        Assert.False(parsed.IsSuccess);
        Assert.Contains("некорректный", parsed.Error);
    }

    [Fact]
    public async Task ReviewedElenaAlenaDuplicateMergesIntoOneAnnotatedMember()
    {
        var parsed = await Parse(Header + "\n" +
            "1\nhttps://t.me/neuro_sofia_art\nЕлена / Lifestyle\n" +
            "2\nhttps://t.me/neuro_sofia_art\nАлена / Lifestyle\n");
        Assert.True(parsed.IsSuccess, parsed.Error);
        var row = Assert.Single(parsed.Entries!.Where(item => item.Position.HasValue));
        Assert.Equal("Елена / Алена", row.DisplayName);
        Assert.False(row.IsPenalty);
        Assert.Equal(1, row.Position);
        Assert.Contains("№1", row.ResolutionNote);
        Assert.Contains("№2", row.ResolutionNote);
        Assert.Contains("не установлена", row.ResolutionNote);
    }

    [Fact]
    public async Task DuplicateAcrossOrdinaryAndPenaltyKeepsPenaltyAndSourcePositions()
    {
        var parsed = await Parse(Header + "\n" +
            "1\nhttps://t.me/neuro_sofia_art\nАлёна / Lifestyle\n" +
            "❌ШТРАФНОЙ ДЕНЬ❌\n" +
            "2\nhttps://t.me/neuro_sofia_art\nЕлена / Lifestyle\n");
        Assert.True(parsed.IsSuccess, parsed.Error);
        var row = Assert.Single(parsed.Entries!.Where(item => item.Position.HasValue));
        Assert.Equal("Елена / Алена", row.DisplayName);
        Assert.True(row.IsPenalty);
        Assert.Equal(2, row.Position); // Status comes from source penalty section.
        Assert.Contains("№1", row.ResolutionNote);
        Assert.Contains("№2", row.ResolutionNote);
        Assert.Contains("ШТРАФНОЙ ДЕНЬ", row.ResolutionNote);
    }

    [Fact]
    public async Task SameUrlWithDifferentNamesRemainsAnError()
    {
        var parsed = await Parse(Header + "\n" +
            "1\nhttps://t.me/neuro_sofia_art\nЕлена / Lifestyle\n" +
            "2\nhttps://t.me/neuro_sofia_art\nМария / Lifestyle\n");
        Assert.False(parsed.IsSuccess);
        Assert.Contains("указан несколько раз", parsed.Error);
    }

    [Fact]
    public async Task DifferentTopicWithSameKnownUrlIsNotMerged()
    {
        var parsed = await Parse(Header + "\n" +
            "1\nhttps://t.me/neuro_sofia_art\nЕлена / Таро\n" +
            "2\nhttps://t.me/neuro_sofia_art\nАлена / Lifestyle\n");
        Assert.False(parsed.IsSuccess);
        Assert.Contains("указан несколько раз", parsed.Error);
    }

    [Fact]
    public async Task ApprovedResolutionsArePersistedInHistoryAndDailyRegister()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulseTg16Repair_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var db = Path.Combine(directory, "test.db");
            var chat = new ConfiguredChat
            {
                TelegramUserId = 456, PeerId = 16, PeerType = "Channel",
                TelegramTitle = "16 Пост чат тгк", SocialNetwork = "Telegram",
                SynchronizationState = ChatSynchronizationState.COMPLETED
            };
            var source = Header + "\n" +
                "1\nhttps://t.me/e_\nГеля / Lifestyle\n" +
                "2\nhttps://t.me/neuro_sofia_art\nЕлена / Lifestyle\n" +
                "3\nhttps://t.me/neuro_sofia_art\nАлена / Lifestyle\n";
            var parsed = await Parse(source);
            Assert.True(parsed.IsSuccess, parsed.Error);
            var candidate = new ParticipantHistoryCandidate(153, null, source, parsed.Entries);
            var history = new ChatParticipantHistoryStore(db);
            var parser = new Chat16TelegramParticipantHistoryParser();
            var rebuilt = await history.RebuildAsync(chat, [candidate], parser.Rule);
            Assert.Equal("COMPLETED", rebuilt.Outcome);
            var current = await history.ReadCurrentAsync(chat);
            Assert.Contains(current, item => item.InstagramUsername == "tg:e_lkina");
            Assert.Single(current.Where(item => item.InstagramUsername == "tg:neuro_sofia_art"));
            await using (var connection = new SqliteConnection($"Data Source={db}"))
            {
                await connection.OpenAsync();
                await using var query = connection.CreateCommand();
                query.CommandText = """
                    SELECT DisplayName, SourceProfileUrl, ResolutionNote
                    FROM ChatParticipantListEntries WHERE InstagramUsername = 'tg:e_lkina'
                    """;
                await using var reader = await query.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Геля / Lifestyle", reader.GetString(0));
                Assert.Equal("https://t.me/e_", reader.GetString(1));
                Assert.Contains("e_lkina", reader.GetString(2));
            }
            await new ChatParticipantDailySyncStore(db).CommitAsync(chat, [candidate], parser.Rule,
                expectedLiveMessageId: null, outcomeDescription: "Исправления применены");
            var live = await new TelegramParticipantReadStore(db).ReadCurrentAsync(chat);
            Assert.Equal(5, live.Count); // Three administrators and two distinct participants.
            Assert.Equal("Елена / Алена", live.Single(item =>
                item.ProfileUrl.EndsWith("/neuro_sofia_art", StringComparison.Ordinal)).DisplayName);
            Assert.Contains("исходном списке", live.Single(item =>
                item.ProfileUrl.EndsWith("/neuro_sofia_art", StringComparison.Ordinal)).ResolutionNote);
        }
        finally { SqliteTestDatabaseCleanup.DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ARepeatedIdentityInTwoOrdinaryRowsRemainsAnError()
    {
        var parsed = await Parse(Header + "\n" +
            "1\nhttps://t.me/same_person\nПолина / Таро\n" +
            "2\nhttps://t.me/same_person\nПолина / Таро");
        Assert.False(parsed.IsSuccess);
        Assert.Contains("№1", parsed.Error);
        Assert.Contains("№2", parsed.Error);
    }

    [Theory]
    [InlineData("Журналист")]
    [InlineData("Послание")]
    [InlineData("Бренд одежды")]
    public async Task AStandaloneDescriptionIsAcceptedWithoutInventingSubject(string description)
    {
        var parsed = await Parse(Header + "\n1\nhttps://t.me/skryabastore\n" + description);
        Assert.True(parsed.IsSuccess, parsed.Error);
        var person = Assert.Single(parsed.Entries!.Where(item => item.Position.HasValue));
        Assert.Equal(description, person.DisplayName);
        Assert.Equal("https://t.me/skryabastore", person.SubscriptionProfileUrl);
    }

    [Fact]
    public async Task NumberingTypoTenOneTwelveIsRepairedOnlyForUniqueEleventhEntry()
    {
        var text = Header + "\n" + Section(Profiles.Take(10)) +
            "1\nhttps://t.me/unique_eleventh\nЖурналист\n" +
            "12\nhttps://t.me/unique_twelfth\nПослание\n" +
            "❌ ШТРАФНОЙ ДЕНЬ ❌\n13\nhttps://t.me/penalty_member\nАлёна / Lifestyle";
        var parsed = await Parse(text);
        Assert.True(parsed.IsSuccess, parsed.Error);
        var numbered = parsed.Entries!.Where(item => item.Position.HasValue).ToArray();
        Assert.Equal(13, numbered.Length);
        Assert.Equal(Enumerable.Range(1, 13), numbered.Select(item => item.Position!.Value));
        Assert.Equal("https://t.me/unique_eleventh", numbered[10].SubscriptionProfileUrl);
        Assert.Equal("Журналист", numbered[10].DisplayName);
        Assert.True(numbered[12].IsPenalty);
    }

    [Fact]
    public async Task NumberingTypoIsNotRepairedWhenNextNumberIsNotTwelve()
    {
        var text = Header + "\n" + Section(Profiles.Take(10)) +
            "1\nhttps://t.me/unique_eleventh\nЖурналист\n" +
            "13\nhttps://t.me/unique_thirteenth\nПослание";
        var parsed = await Parse(text);
        Assert.False(parsed.IsSuccess);
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public async Task NumberingTypoIsNotRepairedWhenProfileWasAlreadyListed()
    {
        var text = Header + "\n" + Section(Profiles.Take(10)) +
            "1\nhttps://t.me/" + Profiles[0] + "\nПовтор\n" +
            "12\nhttps://t.me/unique_twelfth\nПослание";
        var parsed = await Parse(text);
        Assert.False(parsed.IsSuccess);
    }

    [Fact]
    public async Task WeekendSectionIsPartOfMembershipAndNotPenalty()
    {
        var text = Header + "\n1\nhttps://t.me/soul_light_workshop\nОльга / Мастерская\n" +
            "💃🏼 ВЫХОДНЫЕ ДНИ 💃🏼\n2\nhttps://t.me/mksveji\nМила / Свечи";
        var parsed = await Parse(text);
        Assert.True(parsed.IsSuccess, parsed.Error);
        var numbered = parsed.Entries!.Where(item => item.Position.HasValue).ToArray();
        Assert.Equal(2, numbered.Length);
        Assert.False(numbered[0].IsWeekend);
        Assert.True(numbered[1].IsWeekend);
        Assert.False(numbered[1].IsPenalty);
        Assert.Equal("ВЫХОДНЫЕ ДНИ", numbered[1].SourceSection);
    }

    [Fact]
    public async Task InvalidShortTelegramLinkIsReportedAndNeverInferredFromOtherLists()
    {
        var parsed = await Parse(Header +
            "\n1\nhttps://t.me/e_\nГеля / Lifestyle\n" +
            "2\nhttps://t.me/another_person\nДругой / Lifestyle");
        Assert.False(parsed.IsSuccess);
        Assert.Contains("№1", parsed.Error);
        Assert.Contains("некорректный Telegram-адрес", parsed.Error);
        Assert.Contains("https://t.me/e_", parsed.Error);
    }

    [Fact]
    public async Task DuplicateProfileWithDifferentNamesIsRejectedAcrossOrdinaryAndPenaltySections()
    {
        var text = Header +
            "\n1\nhttps://t.me/neuro_sofia_art\nАлёна / Lifestyle\n" +
            "❌ ШТРАФНОЙ ДЕНЬ ❌\n2\nhttps://t.me/neuro_sofia_art\nЕлена / Lifestyle";
        var parsed = await Parse(text);
        Assert.False(parsed.IsSuccess);
        Assert.Contains("neuro_sofia_art", parsed.Error);
        Assert.Contains("Алёна", parsed.Error);
        Assert.Contains("Елена", parsed.Error);
        Assert.Contains("№1", parsed.Error);
        Assert.Contains("№2", parsed.Error);
    }

    [Fact]
    public async Task EmptyPenaltyHeadingIsAccepted()
    {
        var parsed = await Parse(Header +
            "\n1\nhttps://t.me/skryabastore\nБренд одежды\n" +
            "❌ ШТРАФНОЙ ДЕНЬ ❌");
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Single(parsed.Entries!.Where(item => item.Position.HasValue));
    }

    [Fact]
    public void ChatSpecificRoutingDoesNotAffectInstagramOrOtherTelegramChats()
    {
        var chat = new ConfiguredChat
        {
            TelegramUserId = 123, PeerId = 456, PeerType = "Channel",
            SocialNetwork = "Telegram", TelegramTitle = "16 Пост чат тк",
            SynchronizationState = ChatSynchronizationState.COMPLETED
        };
        Assert.True(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(chat));
        Assert.IsType<Chat16TelegramParticipantHistoryParser>(
            ChatParticipantHistoryRuleSelection.CreateConfiguredParser(chat));
        chat.TelegramTitle = "17 Пост чат тк";
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(chat));
        chat.TelegramTitle = "16 Пост чат тк";
        chat.SocialNetwork = "Instagram";
        chat.ParticipantRuleset = ChatParticipantHistoryRuleSelection.CHAT_14_RULESET;
        // The participant list uses Telegram links even if posts are Instagram.
        Assert.True(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(chat));
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat14(chat));
        Assert.IsType<Chat16TelegramParticipantHistoryParser>(
            ChatParticipantHistoryRuleSelection.CreateConfiguredParser(chat));
    }
}
