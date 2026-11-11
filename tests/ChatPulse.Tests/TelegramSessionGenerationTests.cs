using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using ChatPulse.Infrastructure.Telegram;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>
/// AUTH-052: a session-save callback captured by a previous WTelegram client
/// must not recreate or overwrite session data after a local reset.
/// Uses isolated temporary files and never connects to Telegram.
/// </summary>
public sealed class TelegramSessionGenerationTests
{
    [Fact]
    public async Task OldSaveCallback_AfterReset_CannotResurrectDeletedSession()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var files = new TestDirectory();
        using var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        var sessionPath = Path.Combine(files.Path, "telegram.session.dpapi");
        var oldCallback = CaptureSessionSaveCallback(client, sessionPath);

        oldCallback([1, 2, 3]);
        Assert.True(File.Exists(sessionPath));

        await client.ResetSessionAsync(default);
        Assert.False(File.Exists(sessionPath));

        // Simulates an in-flight WTelegram callback that arrives after reset.
        oldCallback([4, 5, 6]);

        Assert.False(File.Exists(sessionPath));
        Assert.False(File.Exists(sessionPath + ".tmp"));
    }

    [Fact]
    public async Task OldSaveCallback_AfterNewGeneration_CannotOverwriteNewSession()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var files = new TestDirectory();
        using var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore());
        var sessionPath = Path.Combine(files.Path, "telegram.session.dpapi");
        var oldCallback = CaptureSessionSaveCallback(client, sessionPath);

        await client.ResetSessionAsync(default);
        var currentCallback = CaptureSessionSaveCallback(client, sessionPath);
        var newSession = new byte[] { 7, 8, 9 };
        currentCallback(newSession);
        var savedBytes = File.ReadAllBytes(sessionPath);

        oldCallback([10, 11, 12]);

        Assert.Equal(savedBytes, File.ReadAllBytes(sessionPath));
        Assert.Equal(newSession, ProtectedData.Unprotect(
            File.ReadAllBytes(sessionPath), null, DataProtectionScope.CurrentUser));
        Assert.False(File.Exists(sessionPath + ".tmp"));
    }

    private static Action<byte[]> CaptureSessionSaveCallback(
        WTelegramAuthenticationClient client, string sessionPath)
    {
        // Production WTelegram client captures the same generation in its callback.
        // Reflection is limited to this implementation-level regression test.
        var type = typeof(WTelegramAuthenticationClient);
        var generationField = type.GetField("_sessionGeneration", BindingFlags.Instance | BindingFlags.NonPublic);
        var saveMethod = type.GetMethod("SaveSession", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(generationField);
        Assert.NotNull(saveMethod);
        var generation = Assert.IsType<long>(generationField.GetValue(client));

        return bytes => { saveMethod.Invoke(client, [sessionPath, bytes, generation]); };
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ChatPulse.Tests", Guid.NewGuid().ToString("N"));

        public TestDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
