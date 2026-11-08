using System.IO;
using System.Reflection;
using ChatPulse.Application.Services;
using ChatPulse.Desktop.Settings;
using ChatPulse.Infrastructure.Telegram;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>
/// Offline shutdown regression tests. No Telegram requests, login codes, or real keys.
/// </summary>
public sealed class TelegramShutdownTests
{
    [Fact]
    public async Task OperationGate_DisposeDuringActiveLease_AllowsSafeRelease()
    {
        using var gate = new TelegramOperationGate();
        var lease = await gate.EnterAsync(default);
        gate.Dispose();

        // Previously a lease releasing a disposed SemaphoreSlim could throw.
        lease.Dispose();
        lease.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => gate.EnterAsync(default));
    }

    [Fact]
    public async Task OperationGate_DisposeWhileAnotherHandlerWaits_UnblocksWaiter()
    {
        using var gate = new TelegramOperationGate();
        using var lease = await gate.EnterAsync(default);
        var waiting = gate.EnterAsync(default);
        Assert.False(waiting.IsCompleted);

        gate.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        // Lease is still held here: shutdown must not depend on its release.
    }

    [Fact]
    public async Task WTelegramClient_DisposeDuringActiveOperation_DefersClientCleanup()
    {
        using var files = new TestDataDirectory();
        var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        var gate = GetClientSemaphore(client);
        await gate.WaitAsync();
        try
        {
            client.Dispose();
            client.Dispose();
            Assert.False(client.DisposeCompletion.IsCompleted);
        }
        finally
        {
            // Simulate an in-flight method reaching its finally block.
            gate.Release();
        }

        await client.DisposeCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAuthorizedUserIdAsync(default));
    }

    [Fact]
    public async Task WTelegramClient_DisposeWithQueuedRequest_DoesNotDisposeSemaphoreUnderWaiter()
    {
        using var files = new TestDataDirectory();
        var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        var gate = GetClientSemaphore(client);
        await gate.WaitAsync();
        var released = false;
        try
        {
            var queued = client.GetAuthorizedUserIdAsync(default);
            Assert.False(queued.IsCompleted);
            client.Dispose();
            Assert.False(client.DisposeCompletion.IsCompleted);
            gate.Release();
            released = true;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
            await client.DisposeCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            // Ensure the gate is released if an assertion failed before Release().
            if (!released) gate.Release();
        }
    }

    [Fact]
    public async Task WTelegramClient_AlreadyDisposed_DoesNotAcceptNewRequests()
    {
        using var files = new TestDataDirectory();
        var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        client.Dispose();
        await client.DisposeCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAuthorizedUserIdAsync(default));
    }

    [Fact]
    public async Task JsonSettings_DisposeDuringActiveOperation_CancelsWaitersWithoutBreakingRelease()
    {
        using var files = new TestDataDirectory();
        using var settings = new JsonSettingsService(files.Path);
        var gate = typeof(JsonSettingsService).GetField("_semaphore", BindingFlags.Instance | BindingFlags.NonPublic);
        var semaphore = Assert.IsType<SemaphoreSlim>(gate!.GetValue(settings));
        await semaphore.WaitAsync();
        try
        {
            var waiting = settings.LoadAsync();
            Assert.False(waiting.IsCompleted);
            settings.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            semaphore.Release();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => settings.LoadAsync());
    }

    private static SemaphoreSlim GetClientSemaphore(WTelegramAuthenticationClient client)
    {
        var field = typeof(WTelegramAuthenticationClient)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<SemaphoreSlim>(field!.GetValue(client));
    }

    private sealed class TestDataDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ChatPulse.Tests", Guid.NewGuid().ToString("N"));

        public TestDataDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
