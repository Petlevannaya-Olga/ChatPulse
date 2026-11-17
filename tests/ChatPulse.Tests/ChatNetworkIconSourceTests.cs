using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatNetworkIconSourceTests
{
    [Theory]
    [InlineData("Instagram")]
    [InlineData("Telegram")]
    [InlineData("TikTok")]
    public void ConnectedChat_UsesStoredSocialNetwork(string network)
    {
        var chat = new ConfiguredChat
        {
            TelegramTitle = "Пост чат",
            SocialNetwork = network
        };

        var row = new SynchronizationChatRow(chat);

        Assert.Equal(network, row.Network);
        Assert.False(row.IsPendingConnection);
    }

    [Theory]
    [InlineData("Instagram")]
    [InlineData("Telegram")]
    [InlineData("TikTok")]
    public void PendingChat_UsesAnalyzedSocialNetwork(string network)
    {
        var pending = new PendingChatConnection
        {
            TelegramTitle = "Пост чат",
            SocialNetwork = network
        };

        var row = new SynchronizationChatRow(pending);

        Assert.Equal(network, row.Network);
        Assert.True(row.IsPendingConnection);
    }

    [Fact]
    public void UnknownNetwork_DoesNotGetAssignedARecognizedSocialNetwork()
    {
        var row = new SynchronizationChatRow(new ConfiguredChat
        {
            TelegramTitle = "Пост чат"
        });

        Assert.Equal(string.Empty, row.Network);
    }
}
