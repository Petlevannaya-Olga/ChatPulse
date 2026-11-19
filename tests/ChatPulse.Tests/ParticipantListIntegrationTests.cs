using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ParticipantListIntegrationTests
{
    private const string REGISTER = """
        14 ЧАТ
        Основатель чатов ОРИОН
        Виктория
        https://www.instagram.com/danilovavica
        🌟VIP УЧАСТНИКИ 🌟
        1 VIP ⭐
        https://www.instagram.com/alinavolta
        2 VIP ⭐
        https://www.instagram.com/nita_health
        ⚡ ВСЕ МЫ ⚡
        1
        https://www.instagram.com/q.r1s_
        Подписка сюда 👆🏼
        Актив дает отсюда 👇🏼
        https://www.instagram.com/__dark__storona__
        2
        https://www.instagram.com/kira.p
        3
        https://www.instagram.com/user_3
        💃 ВЫХОДНЫЕ 💃
        4
        https://www.instagram.com/holiday
        ❌ ШТРАФНЫЕ ДНИ ❌
        5
        https://www.instagram.com/as_dovaaХ
        6
        https://www.instagram.com/b.marina.88Х
        """;

    private const string CHAT_RULES = """
        ✨Это наш ПОСТ ЧАТ✨
        ◇ Сюда мы скидываем ссылки для актива
        ◇ Обязательно 2 ссылки
        ◇ Лишнего ничего не пишем, только просьбы актива
        ◇ Не более 4х ссылок в неделю, любой давности
        ◇ Пост чат закрывается на выходные в пт в 23.00 и открывается в пн в 7.00
        Суббота и Воскресенье выходные в чате
        Аккаунт для репостов: https://www.instagram.com/repost.orion.chat
        """;

    // Full 14th chat example: 29 in "ВСЕ МЫ", 1 new in "ШТРАФНЫЕ ДНИ",
    // 1 duplicate penalty entry, 12 VIP and 6 administration.
    private const string FOURTEENTH_CHAT_REGISTER = """
        ***14 ЧАТ***
        Основатель чатов ОРИОН
        Виктория
        https://www.instagram.com/danilovavica
        Директор чатов 9 вектора
        Татьяна
        https://www.instagram.com/tiipopova
        Администратор 2 линейки
        Виктория
        https://www.instagram.com/voa.mr
        Модератор 14 чата
        Николь
        https://www.instagram.com/poznavaysebya
        Модератор Орион
        Алёна
        https://www.instagram.com/___nataliyaa__
        Модератор Орион
        Акана
        https://www.instagram.com/shary.yalta
        🌟VIP УЧАСТНИКИ 🌟
        1 VIP basic lite ⭐️
        https://www.instagram.com/alinavolta
        2 VIP basic lite ⭐️
        https://www.instagram.com/nita_health
        3 VIP basic lite ⭐️
        https://www.instagram.com/stolyarova_travel
        4 VIP basic lite ⭐️
        https://www.instagram.com/mariabrandmarket74
        5 VIP basic lite ⭐️
        https://www.instagram.com/tyne.tykogda
        6 VIP basic lite ⭐️
        https://www.instagram.com/figure_correction_27
        7 VIP basic lite ⭐️
        https://www.instagram.com/natalypotemkina_
        8 VIP basic lite ⭐️
        https://www.instagram.com/aleksandralakshmi
        9 VIP basic lite ⭐️
        https://www.instagram.com/potolki_nvkz_pavel
        10 VIP basic lite ⭐️
        https://www.instagram.com/_vlks__
        11 VIP basic lite ⭐️
        https://www.instagram.com/katrinapurtova
        12 VIP basic lite ⭐️
        https://www.instagram.com/markpuck19
        ⚡️🦋 ВСЕ МЫ 🦋 ⚡️
        1
        https://www.instagram.com/tatianafrolova_psy
        2
        https://www.instagram.com/marinaburnt
        Подписка сюда 👆🏼
        Актив дает отсюда 👇🏼
        https://www.instagram.com/marinamarina8048
        3
        https://www.instagram.com/krivileva_nastya
        4
        https://www.instagram.com/ditim2025
        5
        https://www.instagram.com/zilya_magic
        Подписка сюда 👆🏼
        Актив дает отсюда 👇🏼
        https://www.instagram.com/dufigiggofixikfi
        6
        https://www.instagram.com/_my.bookdiary
        7
        https://www.instagram.com/v__tymoshenko
        8
        https://www.instagram.com/alina.v.photo
        9
        https://www.instagram.com/_mar_g.o
        Подписка сюда 👆🏼
        Актив дает отсюда 👇🏼
        https://www.instagram.com/mar_fozzz
        Выходные с 22 по 29 октября
        10
        https://www.instagram.com/hair_makhachkala
        11
        https://www.instagram.com/elki_naa
        12
        https://www.instagram.com/mariagorbunovarus
        13
        https://www.instagram.com/bl_on_dy_
        14
        https://www.instagram.com/marina_andreevna92
        15
        https://www.instagram.com/olc464
        16
        https://www.instagram.com/_katerina.boiko_
        17
        https://www.instagram.com/baso.vapsy
        18
        https://www.instagram.com/dilyyaaa_di
        19
        https://www.instagram.com/inga_wb_ozon
        20
        https://www.instagram.com/_kima__6512105_
        21
        https://www.instagram.com/aactress_beauty
        Подписка сюда 👆🏼
        Актив дает отсюда 👇🏼
        https://www.instagram.com/danpachi_
        22
        https://www.instagram.com/soul_light_workshop
        23
        https://www.instagram.com/gridneva7094
        24
        https://www.instagram.com/nate_nates
        25
        https://www.instagram.com/valin_vibe
        26
        https://www.instagram.com/mari.nook
        27
        https://www.instagram.com/anna_andreevna3104
        28
        https://www.instagram.com/panf_love1
        29
        https://www.instagram.com/vi___tragedienne
        💃🏻ВЫХОДНЫЕ
        ❌ШТРАФНЫЕ ДНИ❌
        30
        https://instagram.com/_mar_g.oХ
        31
        https://www.instagram.com/taisaredkokasha
        """;

    [Fact]
    public void FourteenthChat_UnlistedPenaltyParticipantIsIncludedExactlyOnce()
    {
        Assert.True(ParticipantListParser.TryParse(FOURTEENTH_CHAT_REGISTER, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(48, parsed.Participants.Count);
        Assert.Equal(30, parsed.Participants.Count(item => item.Category == "Обычный"));
        Assert.Equal(12, parsed.Participants.Count(item => item.Category.Contains("VIP", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(6, parsed.Participants.Count(item => !item.Position.HasValue));

        var duplicated = Assert.Single(parsed.Participants, item =>
            item.SubscriptionProfileUrl == "https://www.instagram.com/_mar_g.o");
        Assert.Equal(9, duplicated.Position);
        Assert.Equal("https://www.instagram.com/mar_fozzz", duplicated.ActivityProfileUrl);
        Assert.Equal(ParticipantStatus.PENALTY, duplicated.Status);

        var penaltyOnly = Assert.Single(parsed.Participants, item =>
            item.SubscriptionProfileUrl == "https://www.instagram.com/taisaredkokasha");
        Assert.Equal(31, penaltyOnly.Position);
        Assert.Equal("Обычный", penaltyOnly.Category);
        Assert.Equal(ParticipantStatus.PENALTY, penaltyOnly.Status);
    }

    [Fact]
    public async Task FourteenthChat_RegistryPersistsAllThirtyRegularParticipants()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "fourteenth-chat.db");
        var registry = new ChatParticipantRegistry(database);
        var chat = Chat(101);
        Assert.Equal(ParticipantListApplyStatus.APPLIED,
            (await registry.UpdateDetailedAsync(chat,
                [(1842L, FOURTEENTH_CHAT_REGISTER, (DateTime?)null)])).Status);

        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM ChatParticipants
            WHERE TelegramUserId=123 AND PeerId=101 AND IsCurrent=1 AND Category='Обычный'
            """;
        Assert.Equal(30L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT COUNT(*) FROM ChatParticipants
            WHERE TelegramUserId=123 AND PeerId=101 AND IsCurrent=1
              AND SubscriptionProfileUrl='https://www.instagram.com/taisaredkokasha'
              AND Status='PENALTY'
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public void ChatRules_RecognizesRulesButNotRealTasks()
    {
        Assert.True(ChatRulesMessageClassifier.IsChatRules(CHAT_RULES));
        Assert.False(ChatRulesMessageClassifier.IsChatRules(null));
        Assert.False(ChatRulesMessageClassifier.IsChatRules("Аккаунт для репостов: https://www.instagram.com/repost.orion.chat"));
        Assert.False(ChatRulesMessageClassifier.IsChatRules(
            "Реакции на https://www.instagram.com/p/abc123/ и https://www.instagram.com/repost.orion.chat"));
        Assert.False(ChatRulesMessageClassifier.IsChatRules(
            "Это наш пост чат, актив на https://www.instagram.com/p/abc123/"));
        Assert.False(ChatRulesMessageClassifier.IsChatRules(
            "Обязательно 2 ссылки https://www.instagram.com/p/abc123/"));
    }

    [Fact]
    public async Task WorkspaceReader_HidesChatRulesWithoutDeletingImportedHistory()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE ImportedChatMessages (
                    TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                    PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL, Text TEXT,
                    PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
                );
                """;
            await create.ExecuteNonQueryAsync();
            foreach (var (id, content) in new (long, string)[]
                     {
                         (1, CHAT_RULES),
                         (2, "Реакции https://www.instagram.com/p/abc123/"),
                         (3, "Аккаунт для репостов https://www.instagram.com/repost.orion.chat")
                     })
            {
                var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO ImportedChatMessages VALUES (123, 'Channel', 101, $id, $body)";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$body", content);
                await insert.ExecuteNonQueryAsync();
            }
        }

        var tasks = await new WorkspaceTaskReader(database).GetTasksAsync(Chat(101));
        Assert.Equal(new long[] { 2, 3 }, tasks.Select(item => item.MessageId));

        await using var check = new SqliteConnection($"Data Source={database}");
        await check.OpenAsync();
        var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ImportedChatMessages";
        Assert.Equal(3L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public void Register_SeparatesSubscriptionActivityAndPenaltySuffix()
    {
        Assert.True(ParticipantListParser.TryParse(REGISTER, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(9, parsed.Participants.Count);
        var dual = parsed.Participants.Single(item => item.SubscriptionProfileUrl.EndsWith("/q.r1s_", StringComparison.Ordinal));
        Assert.Equal("https://www.instagram.com/__dark__storona__", dual.ActivityProfileUrl);
        Assert.Equal("Обычный", dual.Category);
        Assert.Equal(ParticipantStatus.WEEKEND,
            parsed.Participants.Single(item => item.SubscriptionProfileUrl.EndsWith("/holiday", StringComparison.Ordinal)).Status);
        Assert.Equal(ParticipantStatus.PENALTY,
            parsed.Participants.Single(item => item.SubscriptionProfileUrl.EndsWith("/as_dovaa", StringComparison.Ordinal)).Status);
        Assert.Contains(parsed.Participants, item => item.SubscriptionProfileUrl.EndsWith("/b.marina.88", StringComparison.Ordinal));
        Assert.Contains(parsed.Participants, item => item.DisplayName == "Виктория" && item.Category.Contains("Основатель", StringComparison.Ordinal));
    }

    [Fact]
    public void PenaltyRows_MergeWithOriginalAndPreserveActivityProfile()
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/_mar_g.o
            Подписка сюда 👆🏼
            Актив дает отсюда 👇🏼
            https://www.instagram.com/mar_fozzz
            2
            https://www.instagram.com/accountX
            3
            https://www.instagram.com/third_user
            ❌ ШТРАФНЫЕ ДНИ ❌
            4
            https://www.instagram.com/_mar_g.oХ
            5
            https://www.instagram.com/accountXX
            """;

        Assert.True(ParticipantListParser.TryParse(register, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.Participants.Count);

        var first = parsed.Participants.Single(item => item.SubscriptionProfileUrl.EndsWith("/_mar_g.o", StringComparison.Ordinal));
        Assert.Equal(1, first.Position);
        Assert.Equal("https://www.instagram.com/mar_fozzz", first.ActivityProfileUrl);
        Assert.Equal(ParticipantStatus.PENALTY, first.Status);

        var second = parsed.Participants.Single(item => item.SubscriptionProfileUrl.EndsWith("/accountX", StringComparison.Ordinal));
        Assert.Equal(2, second.Position);
        Assert.Equal(ParticipantStatus.PENALTY, second.Status);
        Assert.Equal(ParticipantStatus.ACTIVE,
            parsed.Participants.Single(item => item.Position == 3).Status);
    }

    [Fact]
    public void PenaltyRows_ExactAndTrimmedBothMatchDifferentPeople_RejectAmbiguousList()
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/person
            2
            https://www.instagram.com/personX
            3
            https://www.instagram.com/third
            ❌ ШТРАФНЫЕ ДНИ ❌
            4
            https://www.instagram.com/personX
            """;
        Assert.False(ParticipantListParser.TryParse(register, out _));
    }

    [Fact]
    public void PenaltyRows_WithoutSuffix_KeepTheExactUserName()
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/personX
            2
            https://www.instagram.com/second
            3
            https://www.instagram.com/third
            ❌ ШТРАФНЫЕ ДНИ ❌
            4
            https://www.instagram.com/personX
            5
            https://www.instagram.com/second
            """;
        Assert.True(ParticipantListParser.TryParse(register, out var parsed));
        Assert.Equal(3, parsed!.Participants.Count);
        Assert.Equal(ParticipantStatus.PENALTY,
            parsed.Participants.Single(item => item.SubscriptionProfileUrl.EndsWith("/personX",
                StringComparison.Ordinal)).Status);
        Assert.Equal(ParticipantStatus.PENALTY,
            parsed.Participants.Single(item => item.SubscriptionProfileUrl.EndsWith("/second",
                StringComparison.Ordinal)).Status);
    }

    [Fact]
    public void PenaltyRows_UnmatchedLatinX_UseConfirmedPageRatherThanTrimmingBlindly()
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/first
            2
            https://www.instagram.com/second
            3
            https://www.instagram.com/third
            ❌ ШТРАФНЫЕ ДНИ ❌
            4
            https://www.instagram.com/new_personX
            """;
        static bool? Opens(string url) => url.EndsWith("/new_person", StringComparison.Ordinal)
            ? true : false;
        Assert.True(ParticipantListParser.TryParse(register, out var parsed, Opens));
        Assert.Contains(parsed!.Participants, item =>
            item.SubscriptionProfileUrl == "https://www.instagram.com/new_person" &&
            item.Status == ParticipantStatus.PENALTY);
        // Without a page checker, the Latin X is left intact: the parser does not
        // silently replace a possibly real username with a different username.
        Assert.True(ParticipantListParser.TryParse(register, out var unverified));
        Assert.Contains(unverified!.Participants, item =>
            item.SubscriptionProfileUrl == "https://www.instagram.com/new_personX" &&
            item.ProfileRequiresVerification);
    }

    [Fact]
    public void PenaltyRows_LatinX_ThatIsPartOfARealProfileIsPreserved()
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/first
            2
            https://www.instagram.com/second
            3
            https://www.instagram.com/third
            ❌ ШТРАФНЫЕ ДНИ ❌
            4
            https://www.instagram.com/actualX
            """;
        static bool? Opens(string url) => url.EndsWith("/actualX", StringComparison.Ordinal);
        Assert.True(ParticipantListParser.TryParse(register, out var parsed, Opens));
        Assert.Contains(parsed!.Participants, item =>
            item.SubscriptionProfileUrl == "https://www.instagram.com/actualX");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    [InlineData(true, null)]
    public void PenaltyRows_AmbiguousOrUnconfirmedPages_DoNotProduceVerifiedRegister(
        bool? fullOpens, bool? shortOpens)
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/first
            2
            https://www.instagram.com/second
            3
            https://www.instagram.com/third
            ❌ ШТРАФНЫЕ ДНИ ❌
            4
            https://www.instagram.com/missingX
            """;
        bool? Opens(string url) => url.EndsWith("/missingX", StringComparison.Ordinal)
            ? fullOpens : shortOpens;
        Assert.False(ParticipantListParser.TryParse(register, out _, Opens));
    }

    [Fact]
    public void PenaltyRows_NoSuffix_DoesNotRequireBrowserEvenWithoutMainListMatch()
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/first
            2
            https://www.instagram.com/second
            3
            https://www.instagram.com/third
            ❌ ШТРАФНЫЕ ДНИ ❌
            4
            https://www.instagram.com/fourth
            """;
        Assert.True(ParticipantListParser.TryParse(register, out var parsed,
            _ => throw new InvalidOperationException("No browser check expected.")));
        Assert.Equal(4, parsed!.Participants.Count);
        var penalty = parsed.Participants.Single(item => item.SubscriptionProfileUrl.EndsWith("/fourth",
            StringComparison.Ordinal));
        Assert.Equal(ParticipantStatus.PENALTY, penalty.Status);
        Assert.False(penalty.ProfileRequiresVerification);
    }

    [Fact]
    public void PenaltyRows_KnownLatinX_DoesNotRequireBrowser()
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/person
            2
            https://www.instagram.com/second
            3
            https://www.instagram.com/third
            ❌ ШТРАФНЫЕ ДНИ ❌
            4
            https://www.instagram.com/personX
            """;
        Assert.True(ParticipantListParser.TryParse(register, out var parsed,
            _ => throw new InvalidOperationException("No browser check expected.")));
        Assert.Equal(3, parsed!.Participants.Count);
        Assert.Equal(ParticipantStatus.PENALTY, parsed.Participants.Single(item =>
            item.SubscriptionProfileUrl.EndsWith("/person", StringComparison.Ordinal)).Status);
    }

    [Fact]
    public void PenaltyRows_UnmatchedLatinX_ProbesOnlyTheTwoPenaltyVariants()
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/normalX
            2
            https://www.instagram.com/second
            3
            https://www.instagram.com/third
            ❌ ШТРАФНЫЕ ДНИ ❌
            4
            https://www.instagram.com/newX
            """;
        var probes = new List<string>();
        bool? Opens(string url)
        {
            probes.Add(url);
            return url.EndsWith("/new", StringComparison.Ordinal);
        }

        Assert.True(ParticipantListParser.TryParse(register, out var parsed, Opens));
        Assert.Equal(new[] { "https://www.instagram.com/newX", "https://www.instagram.com/new" }, probes);
        Assert.Contains(parsed!.Participants, item =>
            item.SubscriptionProfileUrl == "https://www.instagram.com/normalX" &&
            item.Status == ParticipantStatus.ACTIVE);
        Assert.Contains(parsed.Participants, item =>
            item.SubscriptionProfileUrl == "https://www.instagram.com/new" &&
            item.Status == ParticipantStatus.PENALTY);
    }

    [Fact]
    public async Task UnconfirmedPenaltyPage_DoesNotReplacePreviouslySavedRegister()
    {
        const string original = """
            ВСЕ МЫ
            1
            https://www.instagram.com/first
            2
            https://www.instagram.com/second
            3
            https://www.instagram.com/third
            """;
        var revised = original + "\n❌ ШТРАФНЫЕ ДНИ ❌\n4\nhttps://www.instagram.com/newX";
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "penalty.db");
        var chat = Chat(101);
        var registry = new ChatParticipantRegistry(database);
        Assert.Equal(ParticipantListApplyStatus.APPLIED,
            (await registry.UpdateDetailedAsync(chat, [(1L, original, (DateTime?)null)])).Status);
        var pending = await registry.UpdateDetailedAsync(chat,
            [(2L, revised, (DateTime?)null)]);
        Assert.Equal(ParticipantListApplyStatus.PROFILE_CHECK_INCONCLUSIVE, pending.Status);
        Assert.Equal(1L, (await registry.ReadLatestSourceAsync(chat))!.MessageId);
        var failed = await registry.UpdateDetailedAsync(chat,
            [(2L, revised, (DateTime?)null)], profileOpens: _ => null);
        Assert.Equal(ParticipantListApplyStatus.PROFILE_CHECK_INCONCLUSIVE, failed.Status);
        Assert.Equal(1L, (await registry.ReadLatestSourceAsync(chat))!.MessageId);
        var successful = await registry.UpdateDetailedAsync(chat,
            [(2L, revised, (DateTime?)null)],
            profileOpens: url => url.EndsWith("/new", StringComparison.Ordinal));
        Assert.Equal(ParticipantListApplyStatus.APPLIED, successful.Status);
        Assert.Equal(2L, (await registry.ReadLatestSourceAsync(chat))!.MessageId);
    }

    [Fact]
    public async Task Registry_UpdatesPenalizedExistingParticipantWithoutCreatingDuplicate()
    {
        const string original = """
            ВСЕ МЫ
            1
            https://www.instagram.com/_mar_g.o
            Подписка сюда 👆🏼
            Актив дает отсюда 👇🏼
            https://www.instagram.com/mar_fozzz
            2
            https://www.instagram.com/accountX
            3
            https://www.instagram.com/third_user
            """;
        var withPenalty = original + "\n❌ ШТРАФНЫЕ ДНИ ❌\n4\nhttps://www.instagram.com/_mar_g.oХ";
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "participants.db");
        var registry = new ChatParticipantRegistry(database);
        var chat = Chat(101);
        var earlier = new DateTime(2026, 11, 17, 17, 01, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 11, 18, 17, 00, 0, DateTimeKind.Utc);

        Assert.Equal(ParticipantListApplyStatus.APPLIED,
            (await registry.UpdateDetailedAsync(chat, [(1370L, original, (DateTime?)earlier)])).Status);
        var initial = await ReadAsync(database, 101, "https://www.instagram.com/_mar_g.o");

        Assert.Equal(ParticipantListApplyStatus.APPLIED,
            (await registry.UpdateDetailedAsync(chat, [(1408L, withPenalty, (DateTime?)later)])).Status);
        var current = await ReadAsync(database, 101, "https://www.instagram.com/_mar_g.o");
        Assert.Equal(initial.Id, current.Id);
        Assert.Equal(1L, current.Position);
        Assert.Equal("https://www.instagram.com/mar_fozzz", current.Activity);

        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var query = connection.CreateCommand();
        query.CommandText = """
            SELECT COUNT(*), MAX(Status) FROM ChatParticipants
            WHERE TelegramUserId=123 AND PeerId=101 AND IsCurrent=1
              AND SubscriptionProfileUrl='https://www.instagram.com/_mar_g.o'
            """;
        await using (var reader = await query.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("PENALTY", reader.GetString(1));
        }
        var latest = await registry.ReadLatestSourceAsync(chat);
        Assert.NotNull(latest);
        Assert.Equal(1408L, latest.MessageId);
        Assert.Equal(later, latest.SentAtUtc);
    }

    [Fact]
    public void OrdinaryParticipantNames_AreNotTrimmed()
    {
        const string register = """
            ВСЕ МЫ
            1
            https://www.instagram.com/accountX
            2
            https://www.instagram.com/_mar_g.o
            3
            https://www.instagram.com/third_user
            """;
        Assert.True(ParticipantListParser.TryParse(register, out var parsed));
        Assert.Contains(parsed!.Participants, item => item.SubscriptionProfileUrl.EndsWith("/accountX", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_DoesNotConfuseTasksOrShortListsWithParticipantRegisters()
    {
        Assert.False(ParticipantListParser.TryParse("Лайки https://www.instagram.com/p/abc123", out _));
        Assert.False(ParticipantListParser.TryParse("ВСЕ МЫ\n1\nhttps://www.instagram.com/a\n2\nhttps://www.instagram.com/b", out _));
        Assert.False(ParticipantListParser.HasAnyLink("Текст без ссылки"));
        Assert.False(ParticipantListParser.HasAnyLink(null));
        Assert.True(ParticipantListParser.HasAnyLink("https://t.me/channel/123"));
    }

    [Fact]
    public void Parser_RecognizesRegisterStartingWithAdministrativeRoles()
    {
        const string register = """
            ⚡ ОСНОВАТЕЛЬ КОМПАНИИ ⚡
            Виктория / Lifestyle
            https://www.instagram.com/founder
            ДИРЕКТОР 9 ВЕКТОРА
            Татьяна
            https://www.instagram.com/director
            1
            https://www.instagram.com/first
            2
            https://www.instagram.com/second
            3
            https://www.instagram.com/third
            """;
        Assert.True(ParticipantListParser.TryParse(register, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(5, parsed.Participants.Count);
        Assert.Contains(parsed.Participants, item => item.DisplayName == "Виктория / Lifestyle");
        Assert.Contains(parsed.Participants, item => item.DisplayName == "Татьяна");
    }

    [Fact]
    public void Parser_RecognizesAlternativeParticipantListHeading()
    {
        const string register = """
            СПИСОК УЧАСТНИКОВ
            1
            https://www.instagram.com/one
            2
            https://www.instagram.com/two
            3
            https://www.instagram.com/three
            """;
        Assert.True(ParticipantListParser.TryParse(register, out var parsed));
        Assert.Equal(3, parsed!.Participants.Count);
    }

    [Fact]
    public void Parser_DoesNotMistakeAdministrativeAnnouncementForCompleteRegister()
    {
        const string announcement = """
            ОСНОВАТЕЛЬ КОМПАНИИ
            Виктория
            https://www.instagram.com/founder
            ДИРЕКТОР 9 ВЕКТОРА
            Татьяна
            https://www.instagram.com/director
            """;
        Assert.False(ParticipantListParser.TryParse(announcement, out _));
    }

    [Fact]
    public async Task Registry_UpdatesPositionsAndProfilesWithoutChangingParticipantId()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        var chat = Chat(101);
        Assert.Equal(1, await registry.UpdateAsync(chat, [(10, REGISTER)]));
        var first = await ReadAsync(database, 101, "https://www.instagram.com/q.r1s_");
        Assert.Equal(1L, first.Current);
        Assert.Equal(1L, first.Position);
        Assert.Equal("https://www.instagram.com/__dark__storona__", first.Activity);

        var revised = REGISTER.Replace("1\nhttps://www.instagram.com/q.r1s_", "21\nhttps://www.instagram.com/q.r1s_", StringComparison.Ordinal)
            .Replace("https://www.instagram.com/__dark__storona__", "https://www.instagram.com/new_activity", StringComparison.Ordinal);
        Assert.Equal(1, await registry.UpdateAsync(chat, [(11, revised)]));
        var after = await ReadAsync(database, 101, "https://www.instagram.com/q.r1s_");
        Assert.Equal(first.Id, after.Id);
        Assert.Equal(21L, after.Position);
        Assert.Equal("https://www.instagram.com/new_activity", after.Activity);
        // An old list must not overwrite the newer one.
        Assert.Equal(0, await registry.UpdateAsync(chat, [(10, REGISTER)]));
        Assert.Equal(21L, (await ReadAsync(database, 101, "https://www.instagram.com/q.r1s_")).Position);
    }

    [Fact]
    public async Task Synchronization_StoresRegisterAndHidesItFromTasks_RefreshUpdatesRegister()
    {
        using var temp = new TempDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        await settings.SaveChatAsync(Chat(101));
        var telegram = new FakeTelegramClient
        {
            CurrentTelegramUserId = 123,
            ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
                [new TelegramChatOption(101, "Channel", "Пост чат", "Группа")]))
        };
        telegram.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(20, REGISTER), new(21, "Текст без ссылок"),
             new(22, "Актив https://www.instagram.com/p/abc123/")]);
        var database = Path.Combine(temp.Path, "history.db");
        var sync = new ChatSynchronizationService(settings, telegram, database);
        var chat = Assert.Single((await settings.LoadAsync()).Chats);
        await sync.SynchronizeAsync(chat);
        Assert.Equal(22L, Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat)).MessageId);
        Assert.Equal(1L, (await ReadAsync(database, 101, "https://www.instagram.com/q.r1s_")).Position);

        var revised = REGISTER.Replace("1\nhttps://www.instagram.com/q.r1s_", "77\nhttps://www.instagram.com/q.r1s_", StringComparison.Ordinal);
        telegram.ChatMessagesImpl = (_, _, _) => Task.FromResult<IReadOnlyList<TelegramChatMessage>>(
            [new(25, revised), new(22, "Актив https://www.instagram.com/p/abc123/")]);
        await sync.RefreshReactionsAsync(chat);
        Assert.Equal(77L, (await ReadAsync(database, 101, "https://www.instagram.com/q.r1s_")).Position);
        Assert.Equal(22L, Assert.Single(await new WorkspaceTaskReader(database).GetTasksAsync(chat)).MessageId);
    }

    [Fact]
    public async Task WorkspaceReader_SkipsRegisterPhotoAndNoLinkButKeepsLinkedTask()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE ImportedChatMessages (
                    TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                    PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL, Text TEXT,
                    PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
                );
                """;
            await cmd.ExecuteNonQueryAsync();
            foreach (var (id, body) in new (long, string?)[]
                     {
                         (1, null), (2, "Обычный текст без ссылки"),
                         (3, REGISTER), (4, "Лайки https://www.instagram.com/p/abc123/")
                     })
            {
                var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO ImportedChatMessages VALUES (123, 'Channel', 101, $id, $body)";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync();
            }
        }
        var result = await new WorkspaceTaskReader(database).GetTasksAsync(Chat(101));
        Assert.Equal(4L, Assert.Single(result).MessageId);
    }

    [Fact]
    public async Task Registry_DoesNotAffectOtherChats()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        await registry.UpdateAsync(Chat(101), [(10, REGISTER)]);
        await registry.UpdateAsync(Chat(202), [(20, REGISTER)]);
        Assert.Equal(1L, (await ReadAsync(database, 101, "https://www.instagram.com/q.r1s_")).Position);
        Assert.Equal(1L, (await ReadAsync(database, 202, "https://www.instagram.com/q.r1s_")).Position);
    }

    private const string TELEGRAM_REGISTER = """
        ⚡️⚡️⚡️⚡️
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
        1
        https://t.me/BrainStain_with_Polina
        Аполлинария / Физика
        2
        https://t.me/alinafotografff
        Алина / Таро
        3
        https://t.me/dishka_love_13
        Диана / Lifestyle
        4
        https://t.me/+57AnhBiD3zI4ZTQy
        Антон / Тренировки
        5
        https://t.me/neuro_sofia_art
        Елена / Lifestyle
        6
        https://t.me/neuro_sofia_art
        Алена / Lifestyle
        """;

    [Fact]
    public void TelegramRegister_RecognizesNumberedRowsAndKeepsSharedProfiles()
    {
        Assert.True(ParticipantListParser.TryParse(TELEGRAM_REGISTER, out var parsed));
        Assert.NotNull(parsed);
        var numbered = parsed.Participants.Where(item => item.Position.HasValue).ToArray();
        Assert.Equal(6, numbered.Length);
        Assert.Equal("https://t.me/+57AnhBiD3zI4ZTQy", numbered[3].SubscriptionProfileUrl);
        Assert.Equal("Аполлинария / Физика", numbered[0].DisplayName);
        Assert.Equal(2, numbered.Count(item => item.SubscriptionProfileUrl == "https://t.me/neuro_sofia_art"));
        Assert.Contains(parsed.Participants, item => item.DisplayName == "Акана" && item.Category.Contains("МОДЕРАТОР", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TelegramRegister_DuplicateLinksDoNotMergeDifferentPeople()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        var chat = Chat(101);
        Assert.Equal(1, await registry.UpdateAsync(chat, [(30, TELEGRAM_REGISTER)]));
        Assert.Equal(1, await registry.UpdateAsync(chat, [(31, TELEGRAM_REGISTER)]));
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(DISTINCT ParticipantId) FROM ChatParticipants
            WHERE TelegramUserId=123 AND PeerId=101 AND IsCurrent=1
              AND SubscriptionProfileUrl='https://t.me/neuro_sofia_art'
            """;
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public void TelegramRegister_DoesNotExcludeOrdinaryTasksWithTelegramLinks()
    {
        Assert.False(ParticipantListParser.TryParse("Актив https://t.me/mychannel/123", out _));
        Assert.False(ParticipantListParser.TryParse("1\nhttps://t.me/a\n2\nhttps://t.me/b", out _));
    }

    private static ConfiguredChat Chat(long peer) => new()
    {
        TelegramUserId = 123, PeerType = "Channel", PeerId = peer,
        TelegramTitle = "Пост чат", SocialNetwork = "Instagram", HistoryDays = 7
    };

    private static async Task<(long Id, long Position, string Activity, long Current)> ReadAsync(
        string database, long peer, string subscription)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ParticipantId, Position, ActivityProfileUrl, IsCurrent
            FROM ChatParticipants WHERE TelegramUserId=123 AND PeerId=$peer
                AND SubscriptionProfileUrl=$url
            """;
        cmd.Parameters.AddWithValue("$peer", peer);
        cmd.Parameters.AddWithValue("$url", subscription);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ChatPulse-Participants-{Guid.NewGuid():N}");
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
