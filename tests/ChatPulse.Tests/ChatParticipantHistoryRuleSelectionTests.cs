using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatParticipantHistoryRuleSelectionTests
{
    private static ConfiguredChat Chat(string title, string network = "Instagram") => new()
    {
        TelegramUserId = 123, PeerId = 14, PeerType = "Channel",
        TelegramTitle = title, SocialNetwork = network,
        SynchronizationState = ChatSynchronizationState.COMPLETED
    };

    [Theory]
    [InlineData("14 ЧАТ")]
    [InlineData("14 Пост чат б/с")]
    [InlineData("ОРИОН — 14 чат")]
    public void AllowsKnown14ChatTitles(string title)
        => Assert.True(ChatParticipantHistoryRuleSelection.SupportsChat14(Chat(title)));

    [Theory]
    [InlineData("13 Пост чат")]
    [InlineData("114 чат")]
    [InlineData("чат 140")]
    public void DoesNotApplyRulesToOtherChats(string title)
        => Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat14(Chat(title)));

    [Fact]
    public void OnlyCompletedInstagramChatCanBeImported()
    {
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat14(Chat("14 чат", "Telegram")));
        var chat = Chat("14 чат");
        chat.SynchronizationState = ChatSynchronizationState.PENDING;
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat14(chat));
    }
}
