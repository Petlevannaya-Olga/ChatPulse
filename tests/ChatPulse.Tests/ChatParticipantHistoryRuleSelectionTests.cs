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

    [Theory]
    [InlineData("16 Пост чат тк", "Telegram")]
    [InlineData("16 Пост чат тк", "Instagram")]
    [InlineData("16 Пост чат тгк", "Telegram")]
    [InlineData("16 Пост чат тгк", "Instagram")]
    [InlineData("16 ПОСТ ЧАТ ТГК", "Telegram")]
    [InlineData("⚡ 16 • ПОСТ • ЧАТ • ТК ⚡", "Instagram")]
    [InlineData("16   Пост   чат   тк", "Telegram")]
    public void Chat16TelegramListsWorkRegardlessOfPostNetwork(string title, string network)
    {
        var chat = Chat(title, network);
        Assert.True(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(chat));
        Assert.IsType<Chat16TelegramParticipantHistoryParser>(
            ChatParticipantHistoryRuleSelection.CreateConfiguredParser(chat));
    }

    [Fact]
    public void Chat16ActualTelegramTitleEnablesHistoryDespiteLegacyChat14Ruleset()
    {
        var chat = Chat("16 Пост чат тгк", "Instagram");
        chat.ParticipantRuleset = ChatParticipantHistoryRuleSelection.CHAT_14_RULESET;

        Assert.True(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(chat));
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat14(chat));
        Assert.IsType<Chat16TelegramParticipantHistoryParser>(
            ChatParticipantHistoryRuleSelection.CreateConfiguredParser(chat));
    }

    [Fact]
    public void Chat16DisplayNameAlsoEnablesHistoryIfTelegramTitleWasRenamed()
    {
        var chat = Chat("Заголовок из старых настроек");
        chat.DisplayName = "16 Пост чат тгк";

        Assert.True(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(chat));
    }

    [Fact]
    public void Chat16TitleOverridesLegacyChat14RulesetAssignedToInstagramChats()
    {
        var chat = Chat("16 Пост чат тк");
        chat.ParticipantRuleset = ChatParticipantHistoryRuleSelection.CHAT_14_RULESET;
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat14(chat));
        Assert.True(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(chat));
        Assert.IsType<Chat16TelegramParticipantHistoryParser>(
            ChatParticipantHistoryRuleSelection.CreateConfiguredParser(chat));
    }

    [Theory]
    [InlineData("116 Пост чат тк")]
    [InlineData("16 Пост чат тк2")]
    [InlineData("16 Пост чат тктест")]
    [InlineData("116 Пост чат тгк")]
    [InlineData("16 Пост чат тгк2")]
    [InlineData("16 Пост чат тгктест")]
    [InlineData("17 Пост чат тгк")]
    [InlineData("17 Пост чат тк")]
    public void Chat16FormatDoesNotApplyToOtherNumbersOrSuffixes(string title)
        => Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(
            Chat(title, "Telegram")));

    [Fact]
    public void Chat16HistoryRequiresConnectedAuthorizedChat()
    {
        var chat = Chat("16 Пост чат тк", "Telegram");
        chat.SynchronizationState = ChatSynchronizationState.PENDING;
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(chat));
        chat.SynchronizationState = ChatSynchronizationState.COMPLETED;
        chat.TelegramUserId = 0;
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat16Telegram(chat));
    }

    [Fact]
    public void OnlyCompletedInstagramChatCanBeImported()
    {
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat14(Chat("14 чат", "Telegram")));
        var chat = Chat("14 чат");
        chat.SynchronizationState = ChatSynchronizationState.PENDING;
        Assert.False(ChatParticipantHistoryRuleSelection.SupportsChat14(chat));
    }
}
