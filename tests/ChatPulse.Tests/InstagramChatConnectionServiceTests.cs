using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramChatConnectionServiceTests
{
    private static TelegramChatOption InstagramChat => new(444, "Channel", "Пост чат 20", "Канал");

    [Fact]
    public async Task Analysis_DetectsInstagramFromFirstDayAndIgnoresMessagesWithoutLinks()
    {
        using var env = new TestEnvironment();
        var requestedDays = new List<int>();
        env.Telegram.ChatMessagesImpl = (_, days, _) =>
        {
            requestedDays.Add(days);
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(1, "Всем привет, здесь нет ссылки"),
                new(2, "Поставьте лайк https://www.instagram.com/p/a123/")
            ]);
        };
        var result = await env.Connection.AnalyzeAsync(InstagramChat);
        Assert.True(result.IsInstagram);
        Assert.Equal(1, result.Analysis.TaskCount);
        Assert.Collection(requestedDays, day => Assert.Equal(1, day));
    }

    [Fact]
    public async Task Analysis_ExpandsToSevenDaysWhenNoLinkedTaskWasFound()
    {
        using var env = new TestEnvironment();
        var daysRequested = new List<int>();
        env.Telegram.ChatMessagesImpl = (_, days, _) =>
        {
            daysRequested.Add(days);
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>(days == 1
                ? [new(1, "Только новости без ссылок")]
                : [new(2, "Репост https://instagram.com/reel/abc123/")]);
        };
        var result = await env.Connection.AnalyzeAsync(InstagramChat);
        Assert.True(result.IsInstagram);
        Assert.Equal(7, result.DaysScanned);
        Assert.Collection(daysRequested, day => Assert.Equal(1, day), day => Assert.Equal(7, day));
    }

    [Fact]
    public async Task Analysis_AcceptsInstagramPostLinkWithoutActivityKeywords()
    {
        using var env = new TestEnvironment();
        env.Telegram.ChatMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(1, "https://www.instagram.com/p/abc123/"),
                new(2, "Привет, без ссылки")
            ]);
        var result = await env.Connection.AnalyzeAsync(InstagramChat);
        Assert.True(result.IsInstagram);
        Assert.Equal(1, result.DaysScanned);
    }

    [Fact]
    public async Task Analysis_IgnoresPostChatNoticeWithPublicationLinks()
    {
        using var env = new TestEnvironment();
        env.Telegram.ChatMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(1, "✨Это наш ПОСТ ЧАТ✨\nhttps://www.instagram.com/p/example/"),
                new(2, "**Это наш ПОСТ ЧАТ**\nhttps://t.me/channel/123"),
                new(3, "Лайк https://www.instagram.com/reel/abc123/")
            ]);

        var result = await env.Connection.AnalyzeAsync(InstagramChat);

        Assert.True(result.IsInstagram);
        Assert.Equal(1, result.Analysis.TaskCount);
        Assert.Equal(1, result.Analysis.Messages.Count(message => message.PostLinks.Count > 0));
    }

    [Fact]
    public async Task Analysis_DoesNotAcceptOnlyPostChatNoticesAsEvidence()
    {
        using var env = new TestEnvironment();
        var requestedDays = new List<int>();
        env.Telegram.ChatMessagesImpl = (_, days, _) =>
        {
            requestedDays.Add(days);
            return Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(1, "Это наш ПОСТ ЧАТ\nhttps://www.instagram.com/p/example/")
            ]);
        };

        var result = await env.Connection.AnalyzeAsync(InstagramChat);

        Assert.False(result.IsInstagram);
        Assert.Empty(result.Analysis.Messages[0].PostLinks);
        Assert.Equal(new[] { 1, 7 }, requestedDays);
    }

    [Fact]
    public async Task Analysis_RejectsMessagesWithoutTaskLinks()
    {
        using var env = new TestEnvironment();
        env.Telegram.ChatMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(1, "Актив без ссылки"),
                new(2, "Посмотрите https://instagram.com/user_profile")
            ]);
        var result = await env.Connection.AnalyzeAsync(InstagramChat);
        Assert.False(result.IsInstagram);
        Assert.Equal(0, result.Analysis.TaskCount);
    }

    [Fact]
    public async Task Analysis_RejectsTelegramActivityInsteadOfAssumingInstagram()
    {
        using var env = new TestEnvironment();
        env.Telegram.ChatMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(3, "Реакции https://t.me/publicchat/330")
            ]);
        var result = await env.Connection.AnalyzeAsync(InstagramChat);
        Assert.False(result.IsInstagram);
        Assert.Equal(0, (await env.Settings.LoadAsync()).Chats.Count);
    }

    [Fact]
    public async Task ListOfChats_DoesNotOfferAlreadyConnectedChatOrUnrelatedTitle()
    {
        using var env = new TestEnvironment();
        var connected = new ConfiguredChat
        {
            TelegramUserId = 123, PeerId = 444, PeerType = "Channel",
            TelegramTitle = "Пост чат 20", DisplayName = "Пост чат 20",
            SocialNetwork = "Instagram", StartDate = DateOnly.FromDateTime(DateTime.Today)
        };
        await env.Settings.SaveChatAsync(connected);
        await env.Settings.UpdateChatSyncAsync(123, 444, "Channel",
            ChatSynchronizationState.COMPLETED, 1, 20, null);
        var chats = await env.Connection.GetAvailableChatsAsync();
        Assert.DoesNotContain(chats, item => item.PeerId == 444);
        Assert.DoesNotContain(chats, item => item.PeerId == 555);
    }

    [Fact]
    public async Task Connect_ImportsLinkedTasksButSkipsMessagesWithoutLinks()
    {
        using var env = new TestEnvironment();
        env.Telegram.ChatMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([
                new(10, "Привет, хорошего дня!"),
                new(11, "Пожалуйста, лайк https://www.instagram.com/p/abc123/")
            ]);

        Assert.Equal(2, await env.Connection.ConnectAsync(InstagramChat));
        var configured = Assert.Single((await env.Settings.LoadAsync()).Chats);
        Assert.Equal("Instagram", configured.SocialNetwork);
        Assert.Equal(ChatSynchronizationState.COMPLETED, configured.SynchronizationState);
        Assert.Equal(ChatParticipantHistoryRuleSelection.CHAT_14_RULESET, configured.ParticipantRuleset);

        var tasks = await new WorkspaceTaskReader(env.DatabasePath).GetTasksAsync(configured);
        Assert.Single(tasks);
        Assert.Equal(11L, tasks[0].MessageId);
    }

    [Fact]
    public async Task Connect_RejectsUnverifiedChatWithoutChangingSettings()
    {
        using var env = new TestEnvironment();
        env.Telegram.ChatMessagesImpl = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TelegramChatMessage>>([new(1, "Никаких ссылок")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Connection.ConnectAsync(InstagramChat));
        Assert.Empty((await env.Settings.LoadAsync()).Chats);
    }

    [Fact]
    public void ExplicitlyAssignedRuleset_CanBeUsedByAnotherInstagramChat()
    {
        var chat = new ConfiguredChat
        {
            TelegramUserId = 123, PeerId = 444, PeerType = "Channel",
            TelegramTitle = "Пост чат 20", DisplayName = "Пост чат 20",
            SocialNetwork = "Instagram", SynchronizationState = ChatSynchronizationState.COMPLETED,
            ParticipantRuleset = ChatParticipantHistoryRuleSelection.CHAT_14_RULESET
        };
        Assert.True(ChatParticipantHistoryRuleSelection.SupportsChat14(chat));
        chat.ParticipantRuleset = string.Empty;
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat14(chat));
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            $"ChatPulse-ConnectInstagram-{Guid.NewGuid():N}");
        public JsonSettingsService Settings { get; }
        public FakeTelegramClient Telegram { get; }
        public InstagramChatConnectionService Connection { get; }
        public string DatabasePath => Path.Combine(_directory, "chatpulse.db");

        public TestEnvironment()
        {
            Directory.CreateDirectory(_directory);
            Settings = new JsonSettingsService(_directory);
            Telegram = new FakeTelegramClient
            {
                CurrentTelegramUserId = 123,
                ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(123, [
                    InstagramChat, new TelegramChatOption(555, "Channel", "Общий чат", "Канал")]))
            };
            var synchronization = new ChatSynchronizationService(Settings, Telegram,
                DatabasePath);
            Connection = new InstagramChatConnectionService(Telegram, Settings, synchronization);
        }

        public void Dispose()
        {
            Settings.Dispose();
            SqliteTestDatabaseCleanup.DeleteDirectory(_directory);
        }
    }
}
