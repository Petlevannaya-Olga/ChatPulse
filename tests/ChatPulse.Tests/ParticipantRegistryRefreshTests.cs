using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ParticipantRegistryRefreshTests
{
    private const string ORIGINAL_REGISTER = """
        СПИСОК УЧАСТНИЦ
        1
        https://www.instagram.com/first_check
        2
        https://www.instagram.com/second_check
        3
        https://www.instagram.com/third_check
        """;

    private const string UPDATED_REGISTER = """
        СПИСОК УЧАСТНИЦ
        1
        https://www.instagram.com/first_check
        2
        https://www.instagram.com/second_check
        3
        https://www.instagram.com/fourth_check
        """;

    [Fact]
    public async Task RefreshLatestParticipantList_AppliesNewestValidListWithoutReloadingTasks()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        Assert.Equal(1, await registry.UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]));

        var telegram = Client();
        var searched = false;
        telegram.LatestMatchingMessageImpl = (_, matches, _) =>
        {
            searched = true;
            // The remote reader examines newer messages before older ones.
            var messages = new TelegramChatMessage[]
            {
                new(24, "Актив https://www.instagram.com/p/task123/"),
                new(23, UPDATED_REGISTER),
                new(10, ORIGINAL_REGISTER)
            };
            return Task.FromResult<TelegramChatMessage?>(messages.FirstOrDefault(
                item => matches(item.Text)));
        };

        var beforeCursor = (await settings.LoadAsync()).Chats.Single().LastProcessedMessageId;
        var sync = new ChatSynchronizationService(settings, telegram, database);
        var refreshed = await sync.RefreshLatestParticipantListAsync(chat);

        Assert.True(searched);
        Assert.Equal(23L, refreshed.SourceMessageId);
        Assert.True(refreshed.Applied);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "third_check"));
        Assert.Equal(beforeCursor, (await settings.LoadAsync()).Chats.Single().LastProcessedMessageId);
        Assert.Null(telegram.NewMessagesImpl);
    }

    [Fact]
    public async Task LatestTelegramListDateIsStoredAndReturnedAfterRefresh()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var timestamp = new DateTime(2026, 11, 18, 12, 35, 0, DateTimeKind.Utc);
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(31, UPDATED_REGISTER, SentAtUtc: timestamp));
        var progress = new List<ParticipantListSyncProgress>();

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat, progress.Add);

        Assert.Equal(timestamp, result.SourceMessageSentAtUtc);
        Assert.Contains(progress, item => item.Stage == ParticipantListSyncStage.SAVE &&
            item.SourceMessageSentAtUtc == timestamp);
        var stored = await new ChatParticipantRegistry(database).ReadLatestSourceAsync(chat);
        Assert.NotNull(stored);
        Assert.Equal(31L, stored.MessageId);
        Assert.Equal(timestamp, stored.SentAtUtc);
        Assert.Equal(timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), stored.DateLabel);
        Assert.NotNull(stored.LastSuccessfulSyncAtUtc);
    }

    [Fact]
    public async Task OlderMessageDoesNotReplaceLatestListDate()
    {
        using var temp = new TestDirectory();
        var chat = Chat();
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        var newerDate = new DateTime(2026, 11, 18, 12, 35, 0, DateTimeKind.Utc);
        var olderDate = newerDate.AddDays(-1);
        Assert.Equal(1, await registry.UpdateAsync(chat,
            [(30L, UPDATED_REGISTER, (DateTime?)newerDate)]));
        Assert.Equal(0, await registry.UpdateAsync(chat,
            [(10L, ORIGINAL_REGISTER, (DateTime?)olderDate)]));
        var result = await registry.ReadLatestSourceAsync(chat);
        Assert.NotNull(result);
        Assert.Equal(30L, result.MessageId);
        Assert.Equal(newerDate, result.SentAtUtc);
        Assert.NotNull(result.LastSuccessfulSyncAtUtc);
    }

    [Fact]
    public async Task ExistingDatabaseWithoutTimestampColumnIsUpgraded()
    {
        using var temp = new TestDirectory();
        var chat = Chat();
        var database = Path.Combine(temp.Path, "history.db");
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ChatParticipantRegistryVersions (
                    TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                    SourceMessageId INTEGER NOT NULL,
                    PRIMARY KEY (TelegramUserId, PeerType, PeerId)
                );
                INSERT INTO ChatParticipantRegistryVersions VALUES (123, 'Channel', 101, 10);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var registry = new ChatParticipantRegistry(database);
        Assert.Equal("Дата списка неизвестна", (await registry.ReadLatestSourceAsync(chat))?.DateLabel);
        var timestamp = new DateTime(2026, 11, 18, 12, 35, 0, DateTimeKind.Utc);
        await registry.UpdateAsync(chat, [(20L, UPDATED_REGISTER, (DateTime?)timestamp)]);
        var upgraded = await registry.ReadLatestSourceAsync(chat);
        Assert.Equal(timestamp, upgraded?.SentAtUtc);
        Assert.NotNull(upgraded?.LastSuccessfulSyncAtUtc);
    }

    [Fact]
    public async Task RepeatedCurrentList_UpdatesSyncTimeButPreservesOriginalMessageDate()
    {
        using var temp = new TestDirectory();
        var chat = Chat();
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        var sentAt = new DateTime(2026, 11, 18, 12, 35, 0, DateTimeKind.Utc);
        Assert.Equal(1, await registry.UpdateAsync(chat, [(30L, UPDATED_REGISTER, (DateTime?)sentAt)]));

        // Simulate a successful synchronization long ago, then confirm that a
        // second search for the same Telegram message refreshes only sync time.
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE ChatParticipantRegistryVersions SET LastSuccessfulSyncAtUnixSeconds=1";
            await command.ExecuteNonQueryAsync();
        }

        var result = await registry.UpdateDetailedAsync(chat, [(30L, UPDATED_REGISTER, (DateTime?)sentAt)]);
        Assert.Equal(ParticipantListApplyStatus.ALREADY_CURRENT, result.Status);
        var stored = await registry.ReadLatestSourceAsync(chat);
        Assert.NotNull(stored);
        Assert.Equal(30L, stored.MessageId);
        Assert.Equal(sentAt, stored.SentAtUtc);
        Assert.NotNull(stored.LastSuccessfulSyncAtUtc);
        Assert.True(stored.LastSuccessfulSyncAtUtc.Value > DateTime.UnixEpoch);
    }

    [Fact]
    public async Task LegacyListWithoutSyncTime_IsUpdatedWhenSameMessageIsSynchronizedAgain()
    {
        using var temp = new TestDirectory();
        var chat = Chat();
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        var sentAt = new DateTime(2026, 11, 18, 12, 35, 0, DateTimeKind.Utc);
        Assert.Equal(1, await registry.UpdateAsync(chat,
            [(30L, UPDATED_REGISTER, (DateTime?)sentAt)]));

        // Existing lists predate the column that records the sync time.
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ChatParticipantRegistryVersions
                SET LastSuccessfulSyncAtUnixSeconds=NULL
                """;
            await command.ExecuteNonQueryAsync();
        }

        var previous = await registry.ReadLatestSourceAsync(chat);
        Assert.NotNull(previous);
        Assert.Equal(sentAt, previous.SentAtUtc);
        Assert.Null(previous.LastSuccessfulSyncAtUtc);

        var result = await registry.UpdateDetailedAsync(chat,
            [(30L, UPDATED_REGISTER, (DateTime?)sentAt)]);
        Assert.Equal(ParticipantListApplyStatus.ALREADY_CURRENT, result.Status);
        var updated = await registry.ReadLatestSourceAsync(chat);
        Assert.NotNull(updated);
        Assert.Equal(sentAt, updated.SentAtUtc);
        Assert.NotNull(updated.LastSuccessfulSyncAtUtc);
    }

    [Fact]
    public async Task OlderList_DoesNotUpdateSuccessfulSyncTime()
    {
        using var temp = new TestDirectory();
        var chat = Chat();
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        Assert.Equal(1, await registry.UpdateAsync(chat, [(30L, UPDATED_REGISTER, (DateTime?)null)]));
        var before = await registry.ReadLatestSourceAsync(chat);
        Assert.NotNull(before);
        Assert.NotNull(before.LastSuccessfulSyncAtUtc);

        Assert.Equal(0, await registry.UpdateAsync(chat, [(10L, ORIGINAL_REGISTER, (DateTime?)null)]));
        var after = await registry.ReadLatestSourceAsync(chat);
        Assert.Equal(before.LastSuccessfulSyncAtUtc, after?.LastSuccessfulSyncAtUtc);
        Assert.Equal(before.MessageId, after?.MessageId);
    }

    [Fact]
    public async Task RefreshLatestParticipantList_ReportsActualSearchProgressAndSaveStage()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var telegram = Client();
        telegram.ReportLatestMatchingPage = report => report(2, 130);
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(23, UPDATED_REGISTER));
        var progress = new List<ParticipantListSyncProgress>();

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat, progress.Add);

        Assert.True(result.Applied);
        Assert.Equal(23L, result.SourceMessageId);
        Assert.Equal(ParticipantListSyncStage.SETTINGS, progress[0].Stage);
        Assert.Equal(ParticipantListSyncStage.TELEGRAM, progress[1].Stage);
        Assert.Contains(progress, item => item.Stage == ParticipantListSyncStage.SEARCH &&
            item.PagesScanned == 2 && item.MessagesScanned == 130);
        Assert.Equal(ParticipantListSyncStage.SAVE, progress[^1].Stage);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
    }

    [Fact]
    public async Task RefreshLatestParticipantList_CancelledSearchDoesNotReplaceCurrentRegister()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]));
        var telegram = Client();
        using var cancellation = new CancellationTokenSource();
        telegram.LatestMatchingMessageImpl = (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<TelegramChatMessage?>(new(23, UPDATED_REGISTER));
        };
        var progress = new List<ParticipantListSyncProgress>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ChatSynchronizationService(settings, telegram, database)
                .RefreshLatestParticipantListAsync(chat, progress.Add, cancellation.Token));

        Assert.DoesNotContain(progress, item => item.Stage == ParticipantListSyncStage.SAVE);
        Assert.Equal(1L, await CountCurrentAsync(database, "third_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "fourth_check"));
    }

    [Fact]
    public async Task NormalSynchronization_PicksNewestRegisterEvenIfRecentHistoryContainsOlderOne()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]));
        var telegram = Client();
        telegram.NewMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(20, UPDATED_REGISTER)]);
        telegram.ChatMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(10, ORIGINAL_REGISTER)]);

        await new ChatSynchronizationService(settings, telegram, database)
            .SynchronizeLatestAsync(chat);

        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "third_check"));
    }

    [Fact]
    public async Task RefreshLatestParticipantList_WhenNoListFoundPreservesExistingRegister()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]));
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, matches, _) =>
        {
            Assert.False(matches("Актив https://www.instagram.com/p/abc123/"));
            return Task.FromResult<TelegramChatMessage?>(null);
        };

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Null(result.SourceMessageId);
        Assert.False(result.Applied);
        Assert.Equal(1L, await CountCurrentAsync(database, "third_check"));
    }

    [Fact]
    public async Task RefreshLatestParticipantList_OlderListCannotOverwriteCurrent()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(30, UPDATED_REGISTER)]));
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(10, ORIGINAL_REGISTER));

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Equal(10L, result.SourceMessageId);
        Assert.False(result.Applied);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "third_check"));
    }

    [Fact]
    public async Task RefreshLatestParticipantList_EditedMessageWithSameIdUpdatesRegister()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        Assert.Equal(1, await new ChatParticipantRegistry(database)
            .UpdateAsync(chat, [(30, ORIGINAL_REGISTER)]));
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(30, UPDATED_REGISTER));

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Equal(30L, result.SourceMessageId);
        Assert.True(result.Applied);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "third_check"));
    }

    [Fact]
    public async Task RefreshLatestParticipantList_ExplainsWhyNewerListWasNotApplied()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        var savedDate = new DateTime(2026, 11, 17, 14, 1, 0, DateTimeKind.Utc);
        var foundDate = savedDate.AddDays(1);
        Assert.Equal(1, await registry.UpdateAsync(chat,
            [(10L, ORIGINAL_REGISTER, (DateTime?)savedDate)]));

        // Two saved participants now share one subscription profile. The incoming
        // register cannot be matched to one person without guessing.
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE ChatParticipants
                SET SubscriptionProfileUrl='https://www.instagram.com/first_check'
                WHERE ActivityProfileUrl='https://www.instagram.com/second_check'
                """;
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(20, UPDATED_REGISTER, SentAtUtc: foundDate));
        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.False(result.Applied);
        Assert.Equal(ParticipantListApplyStatus.AMBIGUOUS_MATCH, result.Status);
        Assert.Contains("неоднозначно", result.OutcomeDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("№20", result.OutcomeDescription);
        Assert.Contains("№10", result.OutcomeDescription);
        Assert.Equal(10L, result.StoredSource?.MessageId);
        Assert.Equal(savedDate, result.StoredSource?.SentAtUtc);
        Assert.Equal(1L, await CountCurrentAsync(database, "third_check"));
        Assert.Equal(0L, await CountCurrentAsync(database, "fourth_check"));
    }

    [Fact]
    public async Task SameUnchangedTelegramMessageDoesNotReapplyButCanBackfillDate()
    {
        using var temp = new TestDirectory();
        var chat = Chat();
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        var timestamp = new DateTime(2026, 11, 18, 15, 1, 0, DateTimeKind.Utc);
        Assert.Equal(1, await registry.UpdateAsync(chat, [(30, ORIGINAL_REGISTER)]));

        var repeated = await registry.UpdateDetailedAsync(chat,
            [(30L, ORIGINAL_REGISTER, (DateTime?)timestamp)]);

        Assert.Equal(0, repeated.AppliedCount);
        Assert.Equal(ParticipantListApplyStatus.ALREADY_CURRENT, repeated.Status);
        var saved = await registry.ReadLatestSourceAsync(chat);
        Assert.Equal(30L, saved?.MessageId);
        Assert.Equal(timestamp, saved?.SentAtUtc);
    }

    [Fact]
    public async Task NewerValidTelegramMessageUpdatesSourceDateAndAppliedVersion()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        var previous = new DateTime(2026, 11, 17, 17, 1, 0, DateTimeKind.Utc);
        var latest = new DateTime(2026, 11, 18, 19, 2, 0, DateTimeKind.Utc);
        await registry.UpdateAsync(chat, [(10L, ORIGINAL_REGISTER, (DateTime?)previous)]);
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(20, UPDATED_REGISTER, SentAtUtc: latest));

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.True(result.Applied);
        Assert.Equal(ParticipantListApplyStatus.APPLIED, result.Status);
        Assert.Equal(20L, result.StoredSource?.MessageId);
        Assert.Equal(latest, result.StoredSource?.SentAtUtc);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
    }

    [Fact]
    public async Task OlderFoundTelegramMessageExplainsWhySavedDateRemainsNewer()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        var saved = new DateTime(2026, 11, 18, 19, 2, 0, DateTimeKind.Utc);
        await registry.UpdateAsync(chat, [(30L, UPDATED_REGISTER, (DateTime?)saved)]);
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(10, ORIGINAL_REGISTER,
                SentAtUtc: saved.AddDays(-1)));

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Equal(ParticipantListApplyStatus.OLDER_MESSAGE, result.Status);
        Assert.False(result.Applied);
        Assert.Equal(saved, result.StoredSource?.SentAtUtc);
        Assert.Contains("старее", result.OutcomeDescription);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
    }

    [Fact]
    public async Task OtherChat_DamagedPenaltyDomainIsStillRejected()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        const string latest = """
            СПИСОК УЧАСТНИЦ
            1
            https://www.instagram.com/first_check
            2
            https://www.instagram.com/second_check
            3
            https://www.instagram.com/third_check
            ШТРАФНЫЕ ДНИ
            4
            https://www.com/fourth_check
            """;
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(1448, latest));

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Equal(ParticipantListApplyStatus.UNPARSEABLE_MESSAGE, result.Status);
        Assert.Contains("www.com", result.ParseFailureReason);
        Assert.Null(await new ChatParticipantRegistry(database).ReadLatestSourceAsync(chat));
    }

    [Fact]
    public async Task Chat14_DamagedPenaltyDomainInNewestRegisterIsApplied()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        chat.TelegramTitle = "14 Пост чат б/с";
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        const string latest = """
            14 ЧАТ
            VIP УЧАСТНИКИ
            1 VIP
            https://www.instagram.com/vip_check
            ВСЕ МЫ
            1
            https://www.instagram.com/first_check
            2
            https://www.instagram.com/second_check
            3
            https://www.instagram.com/first_check
            ШТРАФНЫЕ ДНИ
            4
            https://www.com/mariagorbunovarus
            """;
        var telegram = Client();
        telegram.ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
            [new TelegramChatOption(101, "Channel", "14 Пост чат б/с", "Группа")]));
        telegram.LatestMatchingMessageImpl = (_, _, _) =>
            Task.FromResult<TelegramChatMessage?>(new(1448, latest));

        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Equal(ParticipantListApplyStatus.APPLIED, result.Status);
        Assert.True(result.Applied);
        Assert.Equal(1448L, (await new ChatParticipantRegistry(database)
            .ReadLatestSourceAsync(chat))?.MessageId);
        Assert.Equal(1L, await CountCurrentAsync(database, "mariagorbunovarus"));
        Assert.Equal(1L, await CountCurrentAsync(database, "first_check"));
    }

    [Fact]
    public async Task MalformedNewestRegisterIsReportedAndCannotBeHiddenByOlderValidRegister()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        Assert.Equal(1, await registry.UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]));
        const string malformed = """
            СПИСОК УЧАСТНИЦ
            1
            https://www.instagram.com/first_check
            2
            https://www.instagram.com/second_check
            3
            https://www.com/third_check
            """;
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, matches, _) =>
        {
            var messages = new[]
            {
                new TelegramChatMessage(30, "Актив https://instagram.com/p/post"),
                new TelegramChatMessage(20, malformed),
                new TelegramChatMessage(10, ORIGINAL_REGISTER)
            };
            return Task.FromResult<TelegramChatMessage?>(messages.FirstOrDefault(item =>
                matches(item.Text)));
        };
        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);

        Assert.Equal(ParticipantListApplyStatus.UNPARSEABLE_MESSAGE, result.Status);
        Assert.Equal(20L, result.SourceMessageId);
        Assert.False(result.Applied);
        Assert.Contains("не удалось разобрать", result.OutcomeDescription);
        Assert.Contains("№20", result.OutcomeDescription);
        Assert.Contains("Причина:", result.OutcomeDescription);
        Assert.Contains("№3", result.OutcomeDescription);
        Assert.Contains("www.com", result.OutcomeDescription);
        Assert.Equal(10L, (await registry.ReadLatestSourceAsync(chat))?.MessageId);
        Assert.Equal(1L, await CountCurrentAsync(database, "third_check"));
        var attempt = await new ChatParticipantRegistry(database).ReadLastAttemptAsync(chat);
        Assert.NotNull(attempt);
        Assert.Equal(ParticipantListApplyStatus.UNPARSEABLE_MESSAGE, attempt.Status);
        Assert.Equal(20L, attempt.MessageId);
        Assert.Contains("Предыдущий список сохранён", attempt.Description);
        Assert.Contains("Причина:", attempt.Description);
        Assert.Contains("www.com", attempt.Description);
        // A new registry instance reads the same persisted report after restart.
        Assert.Equal(attempt.Description, (await new ChatParticipantRegistry(database)
            .ReadLastAttemptAsync(chat))?.Description);
    }

    [Fact]
    public async Task CorrectedRegisterClearsLastParsingFailureAndAcceptsExactDuplicate()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);
        var chat = Chat();
        await settings.SaveChatAsync(chat);
        var database = Path.Combine(temp.Path, "history.db");
        var registry = new ChatParticipantRegistry(database);
        await registry.UpdateAsync(chat, [(10, ORIGINAL_REGISTER)]);
        await registry.SaveLastAttemptAsync(chat, 20, null,
            ParticipantListApplyStatus.UNPARSEABLE_MESSAGE, "Невозможно разобрать");
        const string corrected = """
            СПИСОК УЧАСТНИЦ
            1
            https://www.instagram.com/first_check
            2
            https://www.instagram.com/second_check
            3
            https://www.instagram.com/fourth_check
            4
            https://www.instagram.com/fourth_check
            """;
        var telegram = Client();
        telegram.LatestMatchingMessageImpl = (_, matches, _) =>
            Task.FromResult<TelegramChatMessage?>(new(20, corrected));
        var result = await new ChatSynchronizationService(settings, telegram, database)
            .RefreshLatestParticipantListAsync(chat);
        Assert.Equal(ParticipantListApplyStatus.APPLIED, result.Status);
        Assert.Equal(20L, (await registry.ReadLatestSourceAsync(chat))?.MessageId);
        Assert.Equal(1L, await CountCurrentAsync(database, "fourth_check"));
        Assert.Equal(ParticipantListApplyStatus.APPLIED,
            (await registry.ReadLastAttemptAsync(chat))?.Status);
    }

    private static async Task<long> CountCurrentAsync(string database, string username)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM ChatParticipants WHERE TelegramUserId = 123
                AND PeerType = 'Channel' AND PeerId = 101 AND IsCurrent = 1
                AND ActivityProfileUrl = $profile
            """;
        command.Parameters.AddWithValue("$profile", $"https://www.instagram.com/{username}");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static ConfiguredChat Chat() => new()
    {
        TelegramUserId = 123,
        PeerType = "Channel",
        PeerId = 101,
        TelegramTitle = "Пост чат 101",
        SocialNetwork = "Instagram",
        SynchronizationState = ChatSynchronizationState.COMPLETED,
        HistoryDays = 7
    };

    private static FakeTelegramClient Client() => new()
    {
        CurrentTelegramUserId = 123,
        ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
            [new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа")]))
    };

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-RegistrySync-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
