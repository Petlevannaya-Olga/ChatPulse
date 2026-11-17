using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceTransitionTests
{
    [Fact]
    public async Task CompleteOnboarding_SavesWorkspaceStepAndRestoresItAfterRestart()
    {
        using var temporary = new TestDirectory();
        using (var settings = new JsonSettingsService(temporary.Path))
        {
            await settings.SaveChatAsync(CreateChat(101));
            await settings.SaveChatAsync(CreateChat(102));
            await settings.UpdateChatSyncAsync(123, 101, "Channel", ChatSynchronizationState.COMPLETED, 8, 40, null);
            await settings.UpdateChatSyncAsync(123, 102, "Channel", ChatSynchronizationState.COMPLETED, 9, 41, null);
            await settings.SaveOnboardingStepAsync(OnboardingStep.Synchronization);
            await settings.CompleteOnboardingAsync();
        }

        using var restored = new JsonSettingsService(temporary.Path);
        var state = await restored.LoadAsync();
        Assert.True(state.IsOnboardingCompleted);
        Assert.Equal(OnboardingStep.Workspace, state.CurrentOnboardingStep);
        Assert.Equal(2, state.Chats.Count);
    }

    [Fact]
    public async Task CompleteOnboarding_DoesNotAllowPendingOrFailedChat()
    {
        using var temporary = new TestDirectory();
        using var settings = new JsonSettingsService(temporary.Path);
        await settings.SaveChatAsync(CreateChat(101));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());
        await settings.UpdateChatSyncAsync(123, 101, "Channel", ChatSynchronizationState.COMPLETED, 5, 30, null);
        await settings.SavePendingChatsAsync(123, [new PendingChatConnection
        {
            TelegramUserId = 123, PeerId = 102, PeerType = "Channel",
            TelegramTitle = "Пост чат 102", SocialNetwork = "Instagram", HistoryDays = 7
        }]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());
        var state = await settings.LoadAsync();
        Assert.False(state.IsOnboardingCompleted);
        Assert.NotEqual(OnboardingStep.Workspace, state.CurrentOnboardingStep);
    }

    [Fact]
    public async Task CompleteOnboarding_RejectsNewPeriodUntilHistoryReloadSucceeded()
    {
        using var temporary = new TestDirectory();
        using var settings = new JsonSettingsService(temporary.Path);
        await settings.SaveChatAsync(CreateChat(101));
        await settings.UpdateChatSyncAsync(123, 101, "Channel", ChatSynchronizationState.COMPLETED, 4, 20, null);
        await settings.UpdateChatPeriodAsync(123, 101, "Channel", 20);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CompleteOnboardingAsync());
        Assert.False((await settings.LoadAsync()).IsOnboardingCompleted);

        await settings.ConfirmChatHistoryReloadAsync(123, 101, "Channel", 20, 7, 24);
        await settings.CompleteOnboardingAsync();
        Assert.Equal(OnboardingStep.Workspace, (await settings.LoadAsync()).CurrentOnboardingStep);
    }

    [Fact]
    public async Task WorkspaceReader_ExposesOnlyRecognizedTasksOfTheConfiguredNetwork()
    {
        using var temporary = new TestDirectory();
        var path = Path.Combine(temporary.Path, "history.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ImportedChatMessages (
                    TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL,
                    PeerId INTEGER NOT NULL, MessageId INTEGER NOT NULL, Text TEXT,
                    PRIMARY KEY (TelegramUserId, PeerType, PeerId, MessageId)
                );
                INSERT INTO ImportedChatMessages VALUES
                (123, 'Channel', 101, 1, 'Лайки на https://www.instagram.com/p/abc123/'),
                (123, 'Channel', 101, 2, 'https://www.instagram.com/reel/abc123/'),
                (123, 'Channel', 101, 3, 'Реакции на https://t.me/testchannel/123'),
                (123, 'Channel', 999, 4, 'Лайки https://www.instagram.com/p/def456/');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var result = await new WorkspaceTaskReader(path).GetTasksAsync(CreateChat(101));
        var task = Assert.Single(result);
        Assert.Equal(1, task.MessageId);
        Assert.Contains("instagram.com/p/abc123", task.PostUrl);
    }

    private static ConfiguredChat CreateChat(long id) => new()
    {
        TelegramUserId = 123, PeerId = id, PeerType = "Channel",
        TelegramTitle = $"Пост чат {id}", DisplayName = $"Пост чат {id}",
        SocialNetwork = "Instagram", HistoryDays = 7
    };

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ChatPulse-Workspace-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
