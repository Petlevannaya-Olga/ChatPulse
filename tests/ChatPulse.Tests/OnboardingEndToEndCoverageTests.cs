using System.IO;
using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>
/// Cross-step integration scenario using the real settings service and SQLite, with a fake
/// Telegram transport. Window navigation and real Telegram credentials remain UI/manual tests.
/// </summary>
public sealed class OnboardingEndToEndCoverageTests
{
    [Fact]
    public async Task TwoChats_WithDifferentPeriodsAndOneRetry_CompleteOnlyAfterAllImportsSucceed()
    {
        using var temp = new TestDirectory();
        using var settings = new JsonSettingsService(temp.Path);

        // Step 1: profile and saved navigation.
        await settings.SaveUserNameAndStepAsync("  Ольга ", OnboardingStep.Telegram);
        await settings.SaveTelegramPageAsync(TelegramOnboardingPage.Phone);
        await settings.SaveOnboardingStepAsync(OnboardingStep.FirstChat);

        // Step 3: only recognized, nonconflicting networks are staged, not connected.
        var instagram = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(10, "Лайк https://www.instagram.com/p/abc123/")]);
        var telegram = ChatActivityAnalyzer.Analyze([
            new ChatActivityMessage(20, "Реакции https://t.me/channel/12")]);
        Assert.Equal(ChatActivityClassification.POSSIBLE_ACTIVITY, instagram.Classification);
        Assert.Equal(ChatActivityClassification.POSSIBLE_ACTIVITY, telegram.Classification);
        await settings.SavePendingChatsAsync(123,
        [
            Pending(101, "Instagram"), Pending(202, "Telegram")
        ]);
        Assert.Empty((await settings.LoadAsync()).Chats);

        // Step 4: configure independent periods before making any Telegram history requests.
        await settings.SaveOnboardingStepAsync(OnboardingStep.Synchronization);
        await settings.UpdatePendingChatPeriodAsync(123, 101, "Channel", 3);
        await settings.UpdatePendingChatPeriodAsync(123, 202, "Channel", 19);
        using (var restartedBeforeImport = new JsonSettingsService(temp.Path))
        {
            var saved = await restartedBeforeImport.LoadAsync();
            Assert.Equal(OnboardingStep.Synchronization, saved.CurrentOnboardingStep);
            Assert.Equal(3, saved.PendingChats.Single(chat => chat.PeerId == 101).HistoryDays);
            Assert.Equal(19, saved.PendingChats.Single(chat => chat.PeerId == 202).HistoryDays);
        }

        foreach (var pending in (await settings.LoadAsync()).PendingChats.ToArray())
        {
            await settings.SaveChatAsync(new ConfiguredChat
            {
                TelegramUserId = pending.TelegramUserId,
                PeerId = pending.PeerId,
                PeerType = pending.PeerType,
                TelegramTitle = pending.TelegramTitle,
                DisplayName = pending.TelegramTitle,
                SocialNetwork = pending.SocialNetwork,
                HistoryDays = pending.HistoryDays
            });
            await settings.RemovePendingChatAsync(pending.TelegramUserId, pending.PeerId, pending.PeerType);
        }

        var client = new FakeTelegramClient
        {
            CurrentTelegramUserId = 123,
            ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123,
            [
                new TelegramChatOption(101, "Channel", "Пост чат 101", "Группа"),
                new TelegramChatOption(202, "Channel", "Пост чат 202", "Группа")
            ]))
        };
        var fetches = new List<(long PeerId, int Period)>();
        var failSecondChat = true;
        client.ChatMessagesImpl = (chat, days, _) =>
        {
            fetches.Add((chat.PeerId, days));
            if (chat.PeerId == 202 && failSecondChat)
                throw new IOException("Telegram temporarily unavailable");
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(15, $"message {chat.PeerId}")]);
        };
        var sync = new ChatSynchronizationService(settings, client, temp.Database);
        var configured = (await settings.LoadAsync()).Chats;

        await sync.SynchronizeAsync(configured.Single(chat => chat.PeerId == 101));
        await Assert.ThrowsAsync<IOException>(() =>
            sync.SynchronizeAsync(configured.Single(chat => chat.PeerId == 202)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());
        Assert.False((await settings.LoadAsync()).IsOnboardingCompleted);

        // Retry only the failed chat, never re-request the already completed one.
        failSecondChat = false;
        await sync.SynchronizeAsync(configured.Single(chat => chat.PeerId == 202));
        Assert.Equal(new (long PeerId, int Period)[] { (101, 3), (202, 19), (202, 19) }, fetches);

        await settings.CompleteOnboardingAsync();
        using var reopened = new JsonSettingsService(temp.Path);
        var final = await reopened.LoadAsync();
        Assert.True(final.IsOnboardingCompleted);
        Assert.Equal(OnboardingStep.Workspace, final.CurrentOnboardingStep);
        Assert.Equal("Ольга", final.UserName);
        Assert.Empty(final.PendingChats);
        Assert.All(final.Chats, chat => Assert.Equal(ChatSynchronizationState.COMPLETED, chat.SynchronizationState));

        // Repeated message IDs from different chats are distinct source messages.
        await using var connection = new SqliteConnection($"Data Source={temp.Database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ImportedChatMessages";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static PendingChatConnection Pending(long peerId, string network) => new()
    {
        TelegramUserId = 123,
        PeerId = peerId,
        PeerType = "Channel",
        TelegramTitle = $"Пост чат {peerId}",
        SocialNetwork = network,
        HistoryDays = 7
    };

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Onboarding-EndToEnd-{Guid.NewGuid():N}");
        public string Database => System.IO.Path.Combine(Path, "history.db");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => SqliteTestDatabaseCleanup.DeleteDirectory(Path);
    }
}
