using System.IO;
using ChatPulse.Application;
using ChatPulse.Application.Features.Chats.Save;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.ViewModels;
using ChatPulse.Shared.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatOwnershipTests
{
    private const long AccountA = 1001;
    private const long AccountB = 2002;

    [Fact]
    public async Task SaveChat_SamePeerForDifferentUsers_StoresTwoIndependentConfigurations()
    {
        using var directory = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        await settings.SaveChatAsync(CreateChat(AccountA, "Instagram"));
        await settings.SaveChatAsync(CreateChat(AccountB, "TikTok"));
        var loaded = await settings.LoadAsync();
        Assert.Equal(2, loaded.Chats.Count);
        Assert.Equal("Instagram", Assert.Single(loaded.Chats.Where(c => c.TelegramUserId == AccountA)).SocialNetwork);
        Assert.Equal("TikTok", Assert.Single(loaded.Chats.Where(c => c.TelegramUserId == AccountB)).SocialNetwork);
    }

    [Fact]
    public async Task SaveChat_ResavingSameUsersPeer_UpdatesOnlyThatOwnersRecord()
    {
        using var directory = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        await settings.SaveChatAsync(CreateChat(AccountA, "Instagram"));
        await settings.SaveChatAsync(CreateChat(AccountB, "TikTok"));
        await settings.SaveChatAsync(CreateChat(AccountA, "Telegram"));
        var chats = (await settings.LoadAsync()).Chats;
        Assert.Equal(2, chats.Count);
        Assert.Equal("Telegram", Assert.Single(chats.Where(c => c.TelegramUserId == AccountA)).SocialNetwork);
        Assert.Equal("TikTok", Assert.Single(chats.Where(c => c.TelegramUserId == AccountB)).SocialNetwork);
    }

    [Fact]
    public async Task SaveChat_DifferentPeerTypesWithSameId_AreNotOverwritten()
    {
        using var directory = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        await settings.SaveChatAsync(CreateChat(AccountA, "Instagram"));
        var regularChat = CreateChat(AccountA, "Telegram");
        regularChat.PeerType = "Chat";
        await settings.SaveChatAsync(regularChat);
        var chats = (await settings.LoadAsync()).Chats;
        Assert.Equal(2, chats.Count);
    }

    [Fact]
    public async Task SaveChat_UnownedRecordIsRejectedBeforeWriting()
    {
        using var directory = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => settings.SaveChatAsync(CreateChat(0, "Instagram")));
        Assert.Empty((await settings.LoadAsync()).Chats);
    }

    [Fact]
    public async Task SaveChat_LegacyUnownedConfiguration_RemainsUnassigned()
    {
        using var directory = new TemporarySettingsDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path,
            """
            {"userName":"Test","chats":[{"peerId":500,"peerType":"Channel","telegramTitle":"Old","displayName":"Old","socialNetwork":"Instagram","startDate":"2026-11-01"}]}
            """);
        using var settings = new JsonSettingsService(directory.Path);
        Assert.Equal(0, Assert.Single((await settings.LoadAsync()).Chats).TelegramUserId);
        await settings.SaveChatAsync(CreateChat(AccountA, "Instagram"));
        var all = (await settings.LoadAsync()).Chats;
        Assert.Equal(2, all.Count);
        Assert.Single(all.Where(chat => chat.TelegramUserId == 0));
        Assert.Single(all.Where(chat => chat.TelegramUserId == AccountA));
        Assert.Equal("Test", (await settings.LoadAsync()).UserName);
    }

    [Fact]
    public async Task SaveHandler_ValidOwnerIsPersisted()
    {
        var client = new FakeTelegramClient { CurrentTelegramUserId = AccountA };
        var store = new RecordingChatStore();
        using var gate = new TelegramOperationGate();
        var handler = new SaveChatHandler(store, client, gate);
        var result = await handler.Handle(CreateCommand(AccountA), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(AccountA, Assert.Single(store.Saved).TelegramUserId);
    }

    [Fact]
    public async Task SaveHandler_OwnerChangedBeforeSave_RejectsWithoutPersisting()
    {
        var client = new FakeTelegramClient { CurrentTelegramUserId = AccountB };
        var store = new RecordingChatStore();
        using var gate = new TelegramOperationGate();
        var result = await new SaveChatHandler(store, client, gate).Handle(CreateCommand(AccountA), default);
        Assert.True(result.IsFailure);
        Assert.Equal("telegram.account_changed", result.Error.Code);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task SaveHandler_NotAuthorized_RejectsWithoutPersisting()
    {
        var client = new FakeTelegramClient { CurrentTelegramUserId = 0 };
        var store = new RecordingChatStore();
        using var gate = new TelegramOperationGate();
        var result = await new SaveChatHandler(store, client, gate).Handle(CreateCommand(AccountA), default);
        Assert.True(result.IsFailure);
        Assert.Equal("telegram.not_authorized", result.Error.Code);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task SaveHandler_WaitsForTelegramGate_ThenChecksCurrentOwner()
    {
        var client = new FakeTelegramClient { CurrentTelegramUserId = AccountA };
        var store = new RecordingChatStore();
        using var gate = new TelegramOperationGate();
        var lease = await gate.EnterAsync(default);
        try
        {
            var task = new SaveChatHandler(store, client, gate).Handle(CreateCommand(AccountA), default);
            Assert.False(task.IsCompleted);
            client.CurrentTelegramUserId = AccountB;
            lease.Dispose();
            var result = await task;
            Assert.True(result.IsFailure);
            Assert.Empty(store.Saved);
        }
        finally { lease.Dispose(); }
    }

    [Fact]
    public void SaveValidator_RejectsMissingOwner()
    {
        var result = new SaveChatValidator().Validate(CreateCommand(0));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(SaveChatCommand.ExpectedTelegramUserId));
    }

    [Fact]
    public async Task FirstChatVM_ReenterWithDifferentAccount_DoesNotRestoreFirstAccountsChat()
    {
        using var directory = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        await settings.SaveChatAsync(CreateChat(AccountA, "Instagram"));
        var client = new FakeTelegramClient { CurrentTelegramUserId = AccountA };
        client.ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(client.CurrentTelegramUserId,
            [new TelegramChatOption(500, "Channel", "Same group", "Канал")]));
        using var provider = BuildProvider(client, settings);
        var vm = provider.GetRequiredService<FirstChatSetupViewModel>();
        await vm.EnsureLoadedAsync();
        Assert.True(vm.IsSaved);
        Assert.NotNull(vm.SelectedChat);
        Assert.Equal("Instagram", vm.SocialNetwork);

        client.CurrentTelegramUserId = AccountB;
        vm.Invalidate();
        await vm.EnsureLoadedAsync();
        Assert.False(vm.IsSaved);
        Assert.Null(vm.SelectedChat);
        Assert.Null(vm.SocialNetwork);
        Assert.False(vm.CanSave);
        Assert.Equal(1, (await settings.LoadAsync()).Chats.Count);
    }

    [Fact]
    public async Task FirstChatVM_ReturnToPreviousAccount_RestoresOnlyItsOwnSettings()
    {
        using var directory = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        await settings.SaveChatAsync(CreateChat(AccountA, "Instagram"));
        await settings.SaveChatAsync(CreateChat(AccountB, "TikTok"));
        var client = new FakeTelegramClient { CurrentTelegramUserId = AccountA };
        client.ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(client.CurrentTelegramUserId,
            [new TelegramChatOption(500, "Channel", "Shared", "Канал")]));
        using var provider = BuildProvider(client, settings);
        var vm = provider.GetRequiredService<FirstChatSetupViewModel>();
        await vm.EnsureLoadedAsync();
        Assert.Equal("Instagram", vm.SocialNetwork);

        vm.Invalidate();
        client.CurrentTelegramUserId = AccountB;
        await vm.EnsureLoadedAsync();
        Assert.Equal("TikTok", vm.SocialNetwork);

        vm.Invalidate();
        client.CurrentTelegramUserId = AccountA;
        await vm.EnsureLoadedAsync();
        Assert.Equal("Instagram", vm.SocialNetwork);
        Assert.True(vm.IsSaved);
        Assert.Equal(2, (await settings.LoadAsync()).Chats.Count);
    }

    [Fact]
    public async Task GetChatsQuery_ReturnsUserIdFromSameSnapshot()
    {
        var client = new FakeTelegramClient { CurrentTelegramUserId = AccountA };
        client.ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(AccountA,
            [new TelegramChatOption(500, "Channel", "Group", "Канал")]));
        using var gate = new TelegramOperationGate();
        var result = await new ChatPulse.Application.Features.Telegram.GetChats.GetTelegramChatsHandler(
            client, gate).Handle(new ChatPulse.Application.Features.Telegram.GetChats.GetTelegramChatsQuery(), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(AccountA, result.Value.TelegramUserId);
        Assert.Single(result.Value.Chats);
    }

    [Fact]
    public async Task FirstChatVM_OwnerChangedAfterLoading_RefusesStaleSelectionOnSave()
    {
        using var directory = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        var client = new FakeTelegramClient { CurrentTelegramUserId = AccountA };
        client.ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(client.CurrentTelegramUserId,
            [new TelegramChatOption(500, "Channel", "Group", "Канал")]));
        using var provider = BuildProvider(client, settings);
        var vm = provider.GetRequiredService<FirstChatSetupViewModel>();
        await vm.EnsureLoadedAsync();
        vm.SelectedChat = Assert.Single(vm.Chats);
        vm.SocialNetwork = "Instagram";
        Assert.True(vm.CanSave);
        client.CurrentTelegramUserId = AccountB;
        await vm.SaveAsync();
        Assert.True(vm.HasError);
        Assert.Contains("аккаунт изменился", vm.Error.ToLowerInvariant());
        Assert.False(vm.IsSaved);
        Assert.Empty((await settings.LoadAsync()).Chats);
    }

    [Fact]
    public async Task FirstChatVM_LegacyChat_IsNotAdoptedWithoutSelection()
    {
        using var directory = new TemporarySettingsDirectory();
        using var settings = new JsonSettingsService(directory.Path);
        var path = System.IO.Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path,
            """
            {"chats":[{"peerId":500,"peerType":"Channel","telegramTitle":"Legacy","displayName":"Legacy","socialNetwork":"Instagram","startDate":"2026-11-01"}]}
            """);
        var client = new FakeTelegramClient { CurrentTelegramUserId = AccountB };
        client.ChatsImpl = _ => Task.FromResult(new TelegramChatsSnapshot(AccountB,
            [new TelegramChatOption(500, "Channel", "Legacy", "Канал")]));
        using var provider = BuildProvider(client, settings);
        var vm = provider.GetRequiredService<FirstChatSetupViewModel>();
        await vm.EnsureLoadedAsync();
        Assert.Null(vm.SelectedChat);
        Assert.False(vm.IsSaved);
        Assert.Contains("без привязки", vm.Status);
    }

    private static ConfiguredChat CreateChat(long owner, string network) => new()
    {
        TelegramUserId = owner,
        PeerId = 500,
        PeerType = "Channel",
        TelegramTitle = "Group",
        DisplayName = "Group",
        SocialNetwork = network,
        StartDate = new DateOnly(2026, 11, 1)
    };

    private static SaveChatCommand CreateCommand(long owner)
        => new(500, "Channel", "Group", "Group", "Instagram", new DateOnly(2026, 11, 1), owner);

    private static ServiceProvider BuildProvider(FakeTelegramClient client, ISettingsService settings)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton<ITelegramAuthenticationClient>(client);
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<IChatConfigurationStore, SettingsChatConfigurationStore>();
        services.AddTransient<FirstChatSetupViewModel>();
        return services.BuildServiceProvider();
    }

    private sealed class RecordingChatStore : IChatConfigurationStore
    {
        public List<ConfiguredChat> Saved { get; } = [];
        public Task SaveAsync(long telegramUserId, long peerId, string peerType, string telegramTitle,
            string displayName, string socialNetwork, DateOnly startDate, CancellationToken cancellationToken)
        {
            Saved.Add(new ConfiguredChat
            {
                TelegramUserId = telegramUserId,
                PeerId = peerId,
                PeerType = peerType,
                TelegramTitle = telegramTitle,
                DisplayName = displayName,
                SocialNetwork = socialNetwork,
                StartDate = startDate
            });
            return Task.CompletedTask;
        }
    }

    private sealed class TemporarySettingsDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ChatPulse.Tests." + Guid.NewGuid().ToString("N"));

        public TemporarySettingsDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
