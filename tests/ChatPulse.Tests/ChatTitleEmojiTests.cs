using System.IO;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.Views.Workspace;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatTitleEmojiTests
{
    [Fact]
    public async Task ChatTitle_RoundTripThroughSettingsAndWorkspace_PreservesEmoji()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulse-ChatTitleEmoji-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string telegramTitle = "🌸 Пост 5 чат 💜 (творчество) 👩‍💻";
            var telegramChat = new TelegramChatOption(444, "Channel", telegramTitle, "Канал");
            using var settings = new JsonSettingsService(directory);
            await settings.SaveChatAsync(new ConfiguredChat
            {
                TelegramUserId = 123,
                PeerType = telegramChat.PeerType,
                PeerId = telegramChat.PeerId,
                TelegramTitle = telegramChat.Title,
                DisplayName = telegramChat.Title,
                SocialNetwork = "Instagram"
            });

            var configured = Assert.Single((await settings.LoadAsync()).Chats);
            Assert.Equal(telegramTitle, configured.TelegramTitle);
            Assert.Equal(telegramTitle, configured.DisplayName);
            Assert.Equal(telegramTitle, new WorkspaceChatRow(configured).Title);
            Assert.Contains(telegramTitle, telegramChat.DisplayName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
