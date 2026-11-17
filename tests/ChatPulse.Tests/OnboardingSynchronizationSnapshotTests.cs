using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class OnboardingSynchronizationSnapshotTests
{
    [Fact]
    public void CurrentOwnerOnly_ExcludesForeignAndOwnerlessChats()
    {
        var settings = new AppSettings
        {
            Chats = [Chat(101, 123), Chat(202, 999), Chat(303, 0)],
            PendingChats = [Pending(404, 123), Pending(505, 999), Pending(606, 0)]
        };

        var rows = SynchronizationScreenChatPolicy.CreateRows(settings, 123);

        Assert.Equal(new long[] { 101, 404 }, rows.Select(row => row.PeerId));
        Assert.All(rows, row => Assert.Equal(123L, row.Owner));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void MissingTelegramAccount_NeverRestoresForeignHistory(long owner)
        => Assert.Empty(SynchronizationScreenChatPolicy.CreateRows(
            new AppSettings { Chats = [Chat(101, 123)], PendingChats = [Pending(202, 123)] }, owner));

    [Fact]
    public void AlreadyConfiguredChat_TakesPrecedenceOverPendingDuplicate()
    {
        var settings = new AppSettings
        {
            Chats = [Chat(101, 123, ChatSynchronizationState.COMPLETED)],
            PendingChats = [Pending(101, 123)]
        };

        var row = Assert.Single(SynchronizationScreenChatPolicy.CreateRows(settings, 123));
        Assert.False(row.IsPendingConnection);
        Assert.True(row.IsComplete);
        Assert.False(row.NeedsSynchronization);
    }

    [Fact]
    public void DuplicatePendingCandidate_IsNotShownTwice()
    {
        var settings = new AppSettings { PendingChats = [Pending(101, 123), Pending(101, 123)] };

        Assert.Single(SynchronizationScreenChatPolicy.CreateRows(settings, 123));
    }

    [Fact]
    public void PeerType_IsPartOfChatIdentity()
    {
        var settings = new AppSettings
        {
            Chats = [Chat(101, 123, peerType: "Channel")],
            PendingChats = [Pending(101, 123, peerType: "Chat")]
        };

        var rows = SynchronizationScreenChatPolicy.CreateRows(settings, 123);
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, row => row.PeerType == "Channel" && !row.IsPendingConnection);
        Assert.Contains(rows, row => row.PeerType == "Chat" && row.IsPendingConnection);
    }

    [Fact]
    public void SavedRequestedPeriod_RemainsPendingAfterViewReconstruction()
    {
        var chat = Chat(101, 123, ChatSynchronizationState.COMPLETED);
        chat.RequestedHistoryDays = 22;
        var settings = new AppSettings { Chats = [chat] };

        var row = Assert.Single(SynchronizationScreenChatPolicy.CreateRows(settings, 123));
        Assert.Equal(22, row.Days);
        Assert.True(row.HasPeriodChange);
        Assert.True(row.NeedsSynchronization);
        Assert.Equal(101L, Assert.Single(SynchronizationBatchPlan.SelectPending([row])).PeerId);
    }

    [Fact]
    public void SnapshotConstruction_DoesNotMutateSavedSettings()
    {
        var chat = Chat(101, 123, ChatSynchronizationState.COMPLETED);
        chat.RequestedHistoryDays = 14;
        var settings = new AppSettings { Chats = [chat] };
        var row = Assert.Single(SynchronizationScreenChatPolicy.CreateRows(settings, 123));

        row.Days = 30;

        Assert.Equal(7, chat.HistoryDays);
        Assert.Equal(14, chat.RequestedHistoryDays);
        Assert.Equal(ChatSynchronizationState.COMPLETED, chat.SynchronizationState);
    }

    [Fact]
    public void EmptySettings_NeverEnableContinue()
    {
        var rows = SynchronizationScreenChatPolicy.CreateRows(new AppSettings(), 123);
        Assert.Empty(rows);
        Assert.Empty(SynchronizationBatchPlan.SelectPending(rows));
    }

    [Fact]
    public void SettingsAreRequired()
        => Assert.Throws<ArgumentNullException>(() =>
            SynchronizationScreenChatPolicy.CreateRows(null!, 123));

    private static ConfiguredChat Chat(long id, long owner,
        ChatSynchronizationState state = ChatSynchronizationState.PENDING, string peerType = "Channel") => new()
    {
        TelegramUserId = owner, PeerId = id, PeerType = peerType,
        TelegramTitle = $"Пост чат {id}", DisplayName = $"Пост чат {id}",
        SocialNetwork = "Instagram", HistoryDays = 7, SynchronizationState = state
    };

    private static PendingChatConnection Pending(long id, long owner, string peerType = "Channel") => new()
    {
        TelegramUserId = owner, PeerId = id, PeerType = peerType,
        TelegramTitle = $"Пост чат {id}", SocialNetwork = "Telegram", HistoryDays = 7
    };
}
