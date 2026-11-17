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
        https://www.instagram.com/as_dovaaX
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
        Assert.Equal(new long[] { 3, 2 }, tasks.Select(item => item.MessageId));

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
