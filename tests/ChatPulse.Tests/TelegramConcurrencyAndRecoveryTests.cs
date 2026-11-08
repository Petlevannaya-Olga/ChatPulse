using System.IO;
using ChatPulse.Application.Features.Telegram.ApiCredentials;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Delete;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Save;
using ChatPulse.Application.Features.Telegram.BeginLogin;
using ChatPulse.Application.Features.Telegram.ResetSession;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Infrastructure.Telegram;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramConcurrencyAndRecoveryTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef";
    private const string HashB = "fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task SavingApiKeys_WaitsUntilExistingLoginFinishes()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TelegramOperationGate();
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var telegram = new FakeTelegramClient
        {
            BeginImpl = async (_, _) =>
            {
                started.TrySetResult(true);
                await finish.Task;
                return new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode);
            }
        };
        var journal = new FakeCredentialChangeJournal();
        var loginTask = new BeginTelegramLoginHandler(telegram, gate)
            .Handle(new BeginTelegramLoginCommand("+79991234567"), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var saveTask = new SaveTelegramApiCredentialsHandler(store, telegram, gate, journal)
            .Handle(new SaveTelegramApiCredentialsCommand("456", HashB), default);

        Assert.False(saveTask.IsCompleted);
        Assert.Equal(new TelegramApiCredentials(123, HashA), store.Value);
        Assert.Equal(0, telegram.ResetCalls);

        finish.TrySetResult(true);
        await loginTask;
        var result = await saveTask;
        Assert.True(result.Value);
        Assert.Equal(new TelegramApiCredentials(456, HashB), store.Value);
        Assert.Equal(1, telegram.ResetCalls);
        Assert.False(journal.IsPending);
    }

    [Fact]
    public async Task TwoConcurrentKeyUpdates_CannotInterleaveWriteAndReset()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueReset = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var gate = new TelegramOperationGate();
        var journal = new FakeCredentialChangeJournal();
        var telegram = new FakeTelegramClient
        {
            ResetImpl = async _ =>
            {
                if (!entered.Task.IsCompleted)
                {
                    entered.TrySetResult(true);
                    await continueReset.Task;
                }
            }
        };
        var handler = new SaveTelegramApiCredentialsHandler(store, telegram, gate, journal);
        var first = handler.Handle(new SaveTelegramApiCredentialsCommand("456", HashB), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = handler.Handle(new SaveTelegramApiCredentialsCommand("789", HashA), default);
        Assert.False(second.IsCompleted);
        Assert.Equal(new TelegramApiCredentials(456, HashB), store.Value);

        continueReset.TrySetResult(true);
        await first;
        await second;
        Assert.Equal(new TelegramApiCredentials(789, HashA), store.Value);
        Assert.Equal(2, telegram.ResetCalls);
        Assert.Equal(2, journal.MarkCalls);
        Assert.Equal(2, journal.ClearCalls);
    }

    [Fact]
    public async Task SameKeys_DoNotCreateCrashRecoveryMarker()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var journal = new FakeCredentialChangeJournal();
        var telegram = new FakeTelegramClient();
        var result = await new SaveTelegramApiCredentialsHandler(
            store, telegram, new TelegramOperationGate(), journal)
            .Handle(new SaveTelegramApiCredentialsCommand("0123", HashA.ToUpperInvariant()), default);
        Assert.False(result.Value);
        Assert.Equal(0, journal.MarkCalls);
        Assert.Equal(0, telegram.ResetCalls);
    }

    [Fact]
    public async Task FailureToCreateMarker_DoesNotChangeKeysOrResetSession()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var journal = new FakeCredentialChangeJournal
        {
            BeforeMark = _ => throw new IOException("marker is not writable")
        };
        var telegram = new FakeTelegramClient();
        await Assert.ThrowsAsync<IOException>(() =>
            new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), journal)
                .Handle(new SaveTelegramApiCredentialsCommand("456", HashB), default));
        Assert.Equal(new TelegramApiCredentials(123, HashA), store.Value);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, telegram.ResetCalls);
    }

    [Fact]
    public async Task Save_WriteFailure_RemovesPendingMarkerAndPreservesSession()
    {
        var journal = new FakeCredentialChangeJournal();
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(123, HashA),
            BeforeSave = (_, _) => throw new IOException("save denied")
        };
        var telegram = new FakeTelegramClient();
        await Assert.ThrowsAsync<IOException>(() =>
            new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), journal)
                .Handle(new SaveTelegramApiCredentialsCommand("456", HashB), default));
        Assert.False(journal.IsPending);
        Assert.Equal(0, telegram.ResetCalls);
        Assert.Equal(new TelegramApiCredentials(123, HashA), store.Value);
    }

    [Fact]
    public async Task Save_ResetFailure_RollsBackKeysButKeepsRecoveryMarker()
    {
        var journal = new FakeCredentialChangeJournal();
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var telegram = new FakeTelegramClient { ResetImpl = _ => throw new IOException("session locked") };
        var ex = await Assert.ThrowsAsync<TelegramApiCredentialsUpdateException>(() =>
            new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), journal)
                .Handle(new SaveTelegramApiCredentialsCommand("456", HashB), default));
        Assert.True(ex.PreviousCredentialsRestored);
        Assert.True(journal.IsPending);
        Assert.Equal(new TelegramApiCredentials(123, HashA), store.Value);
    }

    [Fact]
    public async Task Delete_ResetFailure_LeavesMarkerEvenIfKeysRestored()
    {
        var journal = new FakeCredentialChangeJournal();
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var telegram = new FakeTelegramClient { ResetImpl = _ => throw new IOException("session locked") };
        await Assert.ThrowsAsync<TelegramApiCredentialsUpdateException>(() =>
            new DeleteTelegramApiCredentialsHandler(telegram, store, new TelegramOperationGate(), journal)
                .Handle(new DeleteTelegramApiCredentialsCommand(), default));
        Assert.True(journal.IsPending);
        Assert.NotNull(store.Value);
    }

    [Fact]
    public async Task ManualReset_IsAlsoCrashSafe_AndKeepsApiKeys()
    {
        var journal = new FakeCredentialChangeJournal();
        var telegram = new FakeTelegramClient();
        var result = await new ResetTelegramSessionHandler(telegram, new TelegramOperationGate(), journal)
            .Handle(new ResetTelegramSessionCommand(), default);
        Assert.True(result.Value);
        Assert.Equal(1, journal.MarkCalls);
        Assert.Equal(1, journal.ClearCalls);
        Assert.False(journal.IsPending);
    }

    [Fact]
    public async Task ManualReset_FailureLeavesRecoveryMarker()
    {
        var journal = new FakeCredentialChangeJournal();
        var telegram = new FakeTelegramClient { ResetImpl = _ => throw new IOException("session locked") };
        await Assert.ThrowsAsync<IOException>(() =>
            new ResetTelegramSessionHandler(telegram, new TelegramOperationGate(), journal)
                .Handle(new ResetTelegramSessionCommand(), default));
        Assert.True(journal.IsPending);
    }

    [Fact]
    public async Task CrashedChange_IsRecoveredOfflineBeforeAnySessionIsLoaded()
    {
        using var files = new TemporaryDirectory();
        var journal = new FileTelegramCredentialChangeJournal(files.Path);
        var path = Path.Combine(files.Path, "telegram.session.dpapi");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
        await File.WriteAllBytesAsync(path + ".tmp", [4, 3]);
        await journal.MarkPendingAsync(default);
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };

        // Simulates a fresh process. No network, no login code, no WTelegram.Client.
        using var client = new WTelegramAuthenticationClient(files.Path, store, journal);
        var progress = await client.RestoreSessionAsync(default);

        Assert.False(progress.IsAuthorized);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(await journal.IsPendingAsync(default));
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(new TelegramApiCredentials(123, HashA), store.Value);
    }

    [Fact]
    public async Task CrashMarkerWithoutSession_IsRemovedIdempotently()
    {
        using var files = new TemporaryDirectory();
        var journal = new FileTelegramCredentialChangeJournal(files.Path);
        await journal.MarkPendingAsync(default);
        using var client = new WTelegramAuthenticationClient(files.Path, new FakeCredentialsStore(), journal);
        Assert.False((await client.RestoreSessionAsync(default)).IsAuthorized);
        Assert.False(await journal.IsPendingAsync(default));
        Assert.False((await client.RestoreSessionAsync(default)).IsAuthorized);
    }

    [Fact]
    public async Task RecoveryJournal_HasNoCredentialsAndSurvivesRecreation()
    {
        using var files = new TemporaryDirectory();
        var journal = new FileTelegramCredentialChangeJournal(files.Path);
        await journal.MarkPendingAsync(default);
        var nextInstance = new FileTelegramCredentialChangeJournal(files.Path);
        Assert.True(await nextInstance.IsPendingAsync(default));
        var marker = await File.ReadAllTextAsync(Path.Combine(files.Path, "telegram.credentials-update.pending"));
        Assert.Equal("pending", marker);
        Assert.DoesNotContain(HashA, marker);
        await nextInstance.ClearPendingAsync(default);
        Assert.False(await journal.IsPendingAsync(default));
    }

    [Fact]
    public async Task WaitingOnOperationGate_RespectsCancellation()
    {
        var gate = new TelegramOperationGate();
        using var held = await gate.EnterAsync(default);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(cts.Token));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ChatPulse.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

public sealed class ApplicationInstanceLockTests
{
    [Fact]
    public void SecondInstanceIsBlockedUntilFirstReleasesLock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChatPulse.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(ChatPulse.Desktop.Settings.ApplicationInstanceLock.TryAcquire(directory, out var first));
            Assert.NotNull(first);
            using (first)
            {
                Assert.False(ChatPulse.Desktop.Settings.ApplicationInstanceLock.TryAcquire(directory, out var second));
                Assert.Null(second);
            }
            Assert.True(ChatPulse.Desktop.Settings.ApplicationInstanceLock.TryAcquire(directory, out var third));
            Assert.NotNull(third);
            third.Dispose();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

public sealed class TelegramRecoveryCleanupFailureTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef";
    private const string HashB = "fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task MarkerCleanupFailure_DoesNotRollbackValidNewKeys()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var telegram = new FakeTelegramClient();
        var journal = new FakeCredentialChangeJournal
        {
            BeforeClear = _ => throw new IOException("marker cannot be deleted")
        };
        var ex = await Assert.ThrowsAsync<ChatPulse.Application.Features.Telegram.ApiCredentials.TelegramApiCredentialsUpdateException>(
            () => new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), journal)
                .Handle(new SaveTelegramApiCredentialsCommand("456", HashB), default));
        Assert.False(ex.PreviousCredentialsRestored);
        Assert.True(ex.OperationCommitted);
        Assert.Equal(new TelegramApiCredentials(456, HashB), store.Value);
        Assert.Equal(1, telegram.ResetCalls);
        Assert.True(journal.IsPending);
    }
}
