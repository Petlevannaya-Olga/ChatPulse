using System.IO;
using ChatPulse.Application;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatPulse.Tests;

public sealed class FirstChatNavigationRecoveryTests
{
    private static readonly TelegramChatOption PostChat =
        new(501, "Channel", "14 Пост чат", "Канал");

    [Fact]
    public async Task InvalidateDuringLoad_DropsOldResponseAndReloadsCurrentAccount()
    {
        using var directory = new SettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        var oldRequest = new TaskCompletionSource<TelegramChatsSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = new FakeTelegramClient
        {
            ChatsImpl = _ => ++calls == 1
                ? oldRequest.Task
                : Task.FromResult(new TelegramChatsSnapshot(2002,
                    [new TelegramChatOption(502, "Channel", "20 Пост чат", "Канал")]))
        };
        using var provider = BuildProvider(client, settings);
        var vm = provider.GetRequiredService<FirstChatSetupViewModel>();
        var oldLoad = vm.RefreshAsync();
        Assert.True(vm.IsBusy);

        vm.Invalidate(); // account changed / left the screen
        await vm.EnsureLoadedAsync(); // requests a refresh after the old request is released
        oldRequest.SetResult(new TelegramChatsSnapshot(1001, [PostChat]));
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, calls);
        Assert.Equal(2002, vm.CurrentTelegramUserId);
        Assert.Equal(502, Assert.Single(vm.Chats).PeerId);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsSaved);
    }

    [Fact]
    public async Task OfflineRefresh_RejectsRequestThenRecoversWithoutReplacingAccount()
    {
        using var directory = new SettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        var client = new FakeTelegramClient
        {
            CurrentTelegramUserId = 1001,
            ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(1001, [PostChat]))
        };
        using var provider = BuildProvider(client, settings);
        var vm = provider.GetRequiredService<FirstChatSetupViewModel>();
        var network = (FakeTelegramConnectionState)provider.GetRequiredService<ITelegramConnectionState>();
        var restorations = 0;
        vm.TelegramConnectionRestored += (_, _) => restorations++;

        network.SetState(false, false);
        await vm.RefreshAsync();
        Assert.Equal(0, client.GetChatsCalls);
        Assert.Null(vm.CurrentTelegramUserId);
        Assert.False(vm.CanRefresh);

        network.SetState(true, true);
        Assert.Equal(1, restorations);
        await vm.EnsureLoadedAsync();

        Assert.Equal(1, client.GetChatsCalls);
        Assert.Equal(1001, vm.CurrentTelegramUserId);
        Assert.Equal(501, Assert.Single(vm.Chats).PeerId);
        Assert.True(vm.CanRefresh);
    }

    [Fact]
    public void RepeatedOnlineNotification_DoesNotProduceDuplicateRestoredEvents()
    {
        using var directory = new SettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        using var provider = BuildProvider(new FakeTelegramClient(), settings);
        var vm = provider.GetRequiredService<FirstChatSetupViewModel>();
        var connection = (FakeTelegramConnectionState)provider.GetRequiredService<ITelegramConnectionState>();
        var count = 0;
        vm.TelegramConnectionRestored += (_, _) => count++;

        connection.SetState(false, false);
        connection.SetState(true, true);
        connection.SetState(true, true);

        Assert.Equal(1, count);
        Assert.True(vm.IsTelegramAvailable);
    }

    [Fact]
    public async Task ReturningToSameAccount_KeepsSelectionAcrossRefresh()
    {
        using var directory = new SettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        var client = new FakeTelegramClient
        {
            CurrentTelegramUserId = 1001,
            ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(1001, [PostChat]))
        };
        using var provider = BuildProvider(client, settings);
        var vm = provider.GetRequiredService<FirstChatSetupViewModel>();
        await vm.EnsureLoadedAsync();
        vm.SelectedChat = Assert.Single(vm.Chats);

        await vm.RefreshAsync();

        Assert.Equal(501, vm.SelectedChat?.PeerId);
        Assert.Equal(1001, vm.CurrentTelegramUserId);
        Assert.Equal(2, client.GetChatsCalls);
    }

    private static ServiceProvider BuildProvider(FakeTelegramClient client, ISettingsService settings)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton<ITelegramAuthenticationClient>(client);
        services.AddSingleton<ITelegramConnectionState, FakeTelegramConnectionState>();
        services.AddSingleton<ISettingsService>(settings);
        services.AddTransient<FirstChatSetupViewModel>();
        return services.BuildServiceProvider();
    }

    private sealed class SettingsDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ChatPulse.OnboardingLifecycle." + Guid.NewGuid().ToString("N"));

        public SettingsDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
