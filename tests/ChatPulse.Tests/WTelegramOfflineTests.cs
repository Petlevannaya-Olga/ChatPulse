using System.IO;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Infrastructure.Telegram;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>
/// Safe offline tests: never connect to Telegram and never use a real account.
/// Only the paths that return before creating WTelegram.Client are exercised.
/// </summary>
public sealed class WTelegramOfflineTests
{
    [Fact]
    public async Task Restore_WhenThereIsNoSession_DoesNotRequestCredentialsOrConnect()
    {
        using var files = new SandboxDirectory();
        var store = new FakeCredentialsStore();
        using var client = new WTelegramAuthenticationClient(files.Path, store);
        var progress = await client.RestoreSessionAsync(default);
        Assert.False(progress.IsAuthorized);
        Assert.Equal(TelegramLoginInputKind.None, progress.RequiredInput);
        Assert.Equal(0, store.LoadCalls);
    }

    [Fact]
    public async Task Reset_DeletesLocalSessionAndTemporaryFile_WithoutDeletingApiKeys()
    {
        using var files = new SandboxDirectory();
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(123, "0123456789abcdef0123456789abcdef")
        };
        var session = System.IO.Path.Combine(files.Path, "telegram.session.dpapi");
        await File.WriteAllBytesAsync(session, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(session + ".tmp", [5, 6]);
        using var client = new WTelegramAuthenticationClient(files.Path, store);
        await client.ResetSessionAsync(default);
        Assert.False(File.Exists(session));
        Assert.False(File.Exists(session + ".tmp"));
        Assert.NotNull(store.Value);
    }

    [Fact]
    public async Task Reset_Twice_IsSafeWithoutAClient()
    {
        using var files = new SandboxDirectory();
        using var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        await client.ResetSessionAsync(default);
        await client.ResetSessionAsync(default);
    }

    [Fact]
    public async Task Confirm_BeforeBeginningLogin_IsRejectedWithoutNetwork()
    {
        using var files = new SandboxDirectory();
        using var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        var error = await Assert.ThrowsAsync<TelegramAuthenticationException>(
            () => client.ContinueLoginAsync("1234", default));
        Assert.Equal("telegram.login_not_started", error.Code);
    }

    [Fact]
    public async Task Cancel_BeforeLogin_IsRejectedWithoutNetwork()
    {
        using var files = new SandboxDirectory();
        using var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        var error = await Assert.ThrowsAsync<TelegramAuthenticationException>(
            () => client.CancelLoginAsync(default));
        Assert.Equal("telegram.login_not_pending", error.Code);
    }

    [Fact]
    public async Task InvalidDpapiBytes_ProvideSessionCleanupInsteadOfGenericNetworkError()
    {
        using var files = new SandboxDirectory();
        var session = System.IO.Path.Combine(files.Path, "telegram.session.dpapi");
        await File.WriteAllBytesAsync(session, [1, 2, 3, 4]);
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(123, "0123456789abcdef0123456789abcdef")
        };
        using var client = new WTelegramAuthenticationClient(files.Path, store);
        var error = await Assert.ThrowsAsync<TelegramAuthenticationException>(
            () => client.RestoreSessionAsync(default));
        Assert.Equal("telegram.session_unavailable", error.Code);
        Assert.Equal(1, store.LoadCalls);
    }

    [Fact]
    public async Task SessionFileWithoutApiKeys_ReportsConfigurationMissingBeforeReadingSession()
    {
        using var files = new SandboxDirectory();
        var session = System.IO.Path.Combine(files.Path, "telegram.session.dpapi");
        await File.WriteAllBytesAsync(session, [1, 2, 3, 4]);
        var store = new FakeCredentialsStore();
        using var client = new WTelegramAuthenticationClient(files.Path, store);
        var error = await Assert.ThrowsAsync<TelegramAuthenticationException>(
            () => client.RestoreSessionAsync(default));
        Assert.Equal("telegram.configuration_missing", error.Code);
        Assert.Equal(1, store.LoadCalls);
    }

    [Fact]
    public async Task Resend_BeforeCodePending_IsRejectedWithoutNetwork()
    {
        using var files = new SandboxDirectory();
        using var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        var error = await Assert.ThrowsAsync<TelegramAuthenticationException>(
            () => client.ResendCodeAsync(default));
        Assert.Equal("telegram.code_not_pending", error.Code);
    }

    [Fact]
    public async Task GetChats_WithoutAuthorization_IsRejectedWithoutNetwork()
    {
        using var files = new SandboxDirectory();
        using var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        var error = await Assert.ThrowsAsync<TelegramAuthenticationException>(
            () => client.GetChatsAsync(default));
        Assert.Equal("telegram.not_authorized", error.Code);
    }

    [Fact]
    public async Task GetAuthenticatedUserId_WithoutAuthorization_IsRejectedWithoutNetwork()
    {
        using var files = new SandboxDirectory();
        using var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        var error = await Assert.ThrowsAsync<TelegramAuthenticationException>(
            () => client.GetAuthorizedUserIdAsync(default));
        Assert.Equal("telegram.not_authorized", error.Code);
    }

    private sealed class SandboxDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ChatPulse.Tests", Guid.NewGuid().ToString("N"));
        public SandboxDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
