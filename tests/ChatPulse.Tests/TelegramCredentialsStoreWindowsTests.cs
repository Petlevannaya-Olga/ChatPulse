using System.IO;
using ChatPulse.Application.Services;
using ChatPulse.Infrastructure.Telegram;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>
/// These tests use real Windows DPAPI (CurrentUser); no Telegram requests or real user keys.
/// Each test creates an isolated temporary data directory.
/// </summary>
public sealed class TelegramCredentialsStoreWindowsTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef";
    private const string HashB = "fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task SaveLoadDelete_DpapiRoundtrip()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TemporaryDirectory();
        var store = new ProtectedTelegramApiCredentialsStore(fixture.Path);
        Assert.False(await store.IsConfiguredAsync());
        Assert.Null(await store.LoadAsync());

        await store.SaveAsync(new TelegramApiCredentials(123, HashA));
        Assert.True(await store.IsConfiguredAsync());
        Assert.Equal(new TelegramApiCredentials(123, HashA), await store.LoadAsync());
        var content = await File.ReadAllBytesAsync(System.IO.Path.Combine(fixture.Path, "telegram.api.dpapi"));
        Assert.DoesNotContain(HashA, System.Text.Encoding.UTF8.GetString(content));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "telegram.api.dpapi.tmp")));

        await store.SaveAsync(new TelegramApiCredentials(456, HashB));
        Assert.Equal(new TelegramApiCredentials(456, HashB), await store.LoadAsync());
        await store.DeleteAsync();
        Assert.False(await store.IsConfiguredAsync());
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "telegram.api.dpapi")));
        await store.DeleteAsync(); // idempotence
    }

    [Fact]
    public async Task CorruptedProtectedBytes_RejectsWithoutExposingSecrets()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TemporaryDirectory();
        var file = System.IO.Path.Combine(fixture.Path, "telegram.api.dpapi");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4, 5, 6, 7]);
        var store = new ProtectedTelegramApiCredentialsStore(fixture.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.True(File.Exists(file)); // do not silently delete user data
    }

    [Fact]
    public async Task CorruptedProtectedFile_CanBeExplicitlyDeletedByHandler()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TemporaryDirectory();
        var path = System.IO.Path.Combine(fixture.Path, "telegram.api.dpapi");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
        var store = new ProtectedTelegramApiCredentialsStore(fixture.Path);
        var client = new FakeTelegramClient();
        var result = await new ChatPulse.Application.Features.Telegram.ApiCredentials.Delete.DeleteTelegramApiCredentialsHandler(
                client, store, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new ChatPulse.Application.Features.Telegram.ApiCredentials.Delete.DeleteTelegramApiCredentialsCommand(), default);
        Assert.True(result.Value);
        Assert.False(File.Exists(path));
        Assert.Equal(1, client.ResetCalls);
    }

    [Fact]
    public async Task ConcurrentSaves_CannotProduceHalfWrittenCredentialFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TemporaryDirectory();
        var store = new ProtectedTelegramApiCredentialsStore(fixture.Path);
        var a = new TelegramApiCredentials(123, HashA);
        var b = new TelegramApiCredentials(456, HashB);
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => store.SaveAsync(i % 2 == 0 ? a : b)));
        var saved = await store.LoadAsync();
        Assert.True(saved == a || saved == b);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "telegram.api.dpapi.tmp")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ChatPulse.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
