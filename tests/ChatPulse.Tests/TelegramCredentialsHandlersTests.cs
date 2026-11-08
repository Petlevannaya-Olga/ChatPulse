using System.IO;
using ChatPulse.Application.Features.Telegram.ApiCredentials;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Delete;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Get;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Save;
using ChatPulse.Application.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramCredentialsHandlersTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef";
    private const string HashB = "fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task Save_NewKeys_WritesBeforeReset()
    {
        var store = new FakeCredentialsStore();
        var telegram = new FakeTelegramClient
        {
            ResetImpl = _ =>
            {
                Assert.Equal(new TelegramApiCredentials(10, HashA), store.Value);
                return Task.CompletedTask;
            }
        };

        var result = await new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new SaveTelegramApiCredentialsCommand("10", HashA), default);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(1, telegram.ResetCalls);
    }

    [Theory]
    [InlineData("10", "0123456789abcdef0123456789abcdef")]
    [InlineData("010", "0123456789ABCDEF0123456789ABCDEF")]
    public async Task Save_SameKeys_PreservesSession(string apiId, string hash)
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(10, HashA) };
        var telegram = new FakeTelegramClient();

        var result = await new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new SaveTelegramApiCredentialsCommand(apiId, hash), default);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, telegram.ResetCalls);
    }

    [Fact]
    public async Task Save_SameKeysAfterInterruptedUpdate_FirstClearsStaleSession()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(10, HashA) };
        var telegram = new FakeTelegramClient();
        var journal = new FakeCredentialChangeJournal();
        await journal.MarkPendingAsync(default); // Crash after credentials were written.

        var result = await new SaveTelegramApiCredentialsHandler(
                store, telegram, new TelegramOperationGate(), journal)
            .Handle(new SaveTelegramApiCredentialsCommand("10", HashA), default);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value); // Identical keys: no new update.
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(1, telegram.ResetCalls); // But recovery still needs cleanup.
        Assert.False(journal.IsPending);
    }

    [Fact]
    public async Task Save_WhenInterruptedCleanupFails_PreservesRecoveryMarkerAndKeys()
    {
        var previous = new TelegramApiCredentials(10, HashA);
        var store = new FakeCredentialsStore { Value = previous };
        var telegram = new FakeTelegramClient
        {
            ResetImpl = _ => throw new IOException("Cannot delete old session")
        };
        var journal = new FakeCredentialChangeJournal();
        await journal.MarkPendingAsync(default);

        await Assert.ThrowsAsync<IOException>(() =>
            new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), journal)
                .Handle(new SaveTelegramApiCredentialsCommand("10", HashA), default));

        Assert.Equal(previous, store.Value);
        Assert.True(journal.IsPending);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Save_ChangedKeys_ResetsSession()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(10, HashA) };
        var telegram = new FakeTelegramClient();
        var result = await new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new SaveTelegramApiCredentialsCommand("11", HashB), default);
        Assert.True(result.Value);
        Assert.Equal(new TelegramApiCredentials(11, HashB), store.Value);
        Assert.Equal(1, telegram.ResetCalls);
    }

    [Fact]
    public async Task Save_WriteFails_DoesNotResetExistingSession()
    {
        var previous = new TelegramApiCredentials(10, HashA);
        var store = new FakeCredentialsStore
        {
            Value = previous,
            BeforeSave = (_, _) => throw new IOException("disk full")
        };
        var telegram = new FakeTelegramClient();
        await Assert.ThrowsAsync<IOException>(() =>
            new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), new FakeCredentialChangeJournal())
                .Handle(new SaveTelegramApiCredentialsCommand("11", HashB), default));
        Assert.Equal(previous, store.Value);
        Assert.Equal(0, telegram.ResetCalls);
    }

    [Fact]
    public async Task Save_ResetFails_RestoresPreviousCredentials()
    {
        var previous = new TelegramApiCredentials(10, HashA);
        var store = new FakeCredentialsStore { Value = previous };
        var telegram = new FakeTelegramClient
        {
            ResetImpl = _ => throw new IOException("locked session file")
        };
        var ex = await Assert.ThrowsAsync<TelegramApiCredentialsUpdateException>(() =>
            new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), new FakeCredentialChangeJournal())
                .Handle(new SaveTelegramApiCredentialsCommand("11", HashB), default));
        Assert.True(ex.PreviousCredentialsRestored);
        Assert.Equal(previous, store.Value);
        Assert.Equal(2, store.SaveCalls);
    }

    [Fact]
    public async Task Save_FirstConfigurationResetFails_RemovesNewCredentials()
    {
        var store = new FakeCredentialsStore();
        var telegram = new FakeTelegramClient
        {
            ResetImpl = _ => throw new IOException("locked session file")
        };
        var ex = await Assert.ThrowsAsync<TelegramApiCredentialsUpdateException>(() =>
            new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), new FakeCredentialChangeJournal())
                .Handle(new SaveTelegramApiCredentialsCommand("11", HashB), default));
        Assert.True(ex.PreviousCredentialsRestored);
        Assert.Null(store.Value);
        Assert.Equal(1, store.DeleteCalls);
    }

    [Fact]
    public async Task Save_ResetAndRollbackFail_ReportsUncertainState()
    {
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(10, HashA),
            BeforeSave = (call, _) => call == 2 ? throw new IOException("rollback failed") : Task.CompletedTask
        };
        var telegram = new FakeTelegramClient
        {
            ResetImpl = _ => throw new IOException("reset failed")
        };
        var ex = await Assert.ThrowsAsync<TelegramApiCredentialsUpdateException>(() =>
            new SaveTelegramApiCredentialsHandler(store, telegram, new TelegramOperationGate(), new FakeCredentialChangeJournal())
                .Handle(new SaveTelegramApiCredentialsCommand("11", HashB), default));
        Assert.False(ex.PreviousCredentialsRestored);
        Assert.IsType<AggregateException>(ex.InnerException);
    }

    [Fact]
    public async Task Save_PreCanceledToken_DoesNotChangeState()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(10, HashA) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SaveTelegramApiCredentialsHandler(store, new FakeTelegramClient(), new TelegramOperationGate(), new FakeCredentialChangeJournal())
                .Handle(new SaveTelegramApiCredentialsCommand("11", HashB), cts.Token));
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Save_CancellationAfterWritingKeys_StillResetsSession()
    {
        using var cts = new CancellationTokenSource();
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(10, HashA),
            BeforeSave = (_, _) => { cts.Cancel(); return Task.CompletedTask; }
        };
        var client = new FakeTelegramClient
        {
            ResetImpl = token =>
            {
                Assert.False(token.CanBeCanceled);
                return Task.CompletedTask;
            }
        };
        var result = await new SaveTelegramApiCredentialsHandler(store, client, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new SaveTelegramApiCredentialsCommand("11", HashB), cts.Token);
        Assert.True(result.Value);
        Assert.Equal(1, client.ResetCalls);
    }

    [Fact]
    public async Task Delete_CancellationAfterDeletingKeys_StillResetsSession()
    {
        using var cts = new CancellationTokenSource();
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(10, HashA),
            BeforeDelete = _ => { cts.Cancel(); return Task.CompletedTask; }
        };
        var client = new FakeTelegramClient
        {
            ResetImpl = token =>
            {
                Assert.False(token.CanBeCanceled);
                return Task.CompletedTask;
            }
        };
        var result = await new DeleteTelegramApiCredentialsHandler(client, store, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new DeleteTelegramApiCredentialsCommand(), cts.Token);
        Assert.True(result.Value);
        Assert.Equal(1, client.ResetCalls);
    }

    [Fact]
    public async Task Delete_DeletesKeysBeforeReset()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(10, HashA) };
        var telegram = new FakeTelegramClient
        {
            ResetImpl = _ =>
            {
                Assert.Null(store.Value);
                return Task.CompletedTask;
            }
        };
        var result = await new DeleteTelegramApiCredentialsHandler(telegram, store, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new DeleteTelegramApiCredentialsCommand(), default);
        Assert.True(result.Value);
        Assert.Equal(1, telegram.ResetCalls);
        Assert.Equal(1, store.DeleteCalls);
    }

    [Fact]
    public async Task Delete_AfterInterruptedUpdate_CleansOldSessionBeforeRemovingKeys()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(10, HashA) };
        var telegram = new FakeTelegramClient();
        var journal = new FakeCredentialChangeJournal();
        await journal.MarkPendingAsync(default);

        var result = await new DeleteTelegramApiCredentialsHandler(
                telegram, store, new TelegramOperationGate(), journal)
            .Handle(new DeleteTelegramApiCredentialsCommand(), default);

        Assert.True(result.IsSuccess);
        Assert.Null(store.Value);
        Assert.Equal(2, telegram.ResetCalls); // Recovery then explicit deletion.
        Assert.False(journal.IsPending);
    }

    [Fact]
    public async Task Delete_NoExistingKeys_StillClearsOrphanedSession()
    {
        var store = new FakeCredentialsStore();
        var telegram = new FakeTelegramClient();
        var result = await new DeleteTelegramApiCredentialsHandler(telegram, store, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new DeleteTelegramApiCredentialsCommand(), default);
        Assert.True(result.Value);
        Assert.Equal(1, telegram.ResetCalls);
    }

    [Fact]
    public async Task Delete_CorruptedCredentials_CanStillClearFilesAndSession()
    {
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(10, HashA),
            BeforeLoad = () => throw new InvalidDataException("DPAPI corrupted")
        };
        var client = new FakeTelegramClient();
        var result = await new DeleteTelegramApiCredentialsHandler(client, store, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new DeleteTelegramApiCredentialsCommand(), default);
        Assert.True(result.Value);
        Assert.Null(store.Value);
        Assert.Equal(1, client.ResetCalls);
    }

    [Fact]
    public async Task Delete_StoreError_DoesNotResetSession()
    {
        var previous = new TelegramApiCredentials(10, HashA);
        var store = new FakeCredentialsStore
        {
            Value = previous,
            BeforeDelete = _ => throw new UnauthorizedAccessException()
        };
        var telegram = new FakeTelegramClient();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new DeleteTelegramApiCredentialsHandler(telegram, store, new TelegramOperationGate(), new FakeCredentialChangeJournal())
                .Handle(new DeleteTelegramApiCredentialsCommand(), default));
        Assert.Equal(previous, store.Value);
        Assert.Equal(0, telegram.ResetCalls);
    }

    [Fact]
    public async Task Delete_ResetFails_RestoresKeys()
    {
        var previous = new TelegramApiCredentials(10, HashA);
        var store = new FakeCredentialsStore { Value = previous };
        var telegram = new FakeTelegramClient
        {
            ResetImpl = _ => throw new IOException("reset failed")
        };
        var ex = await Assert.ThrowsAsync<TelegramApiCredentialsUpdateException>(() =>
            new DeleteTelegramApiCredentialsHandler(telegram, store, new TelegramOperationGate(), new FakeCredentialChangeJournal())
                .Handle(new DeleteTelegramApiCredentialsCommand(), default));
        Assert.True(ex.PreviousCredentialsRestored);
        Assert.Equal(previous, store.Value);
    }

    [Fact]
    public async Task Delete_ResetAndRollbackFail_ReportsUncertainState()
    {
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(10, HashA),
            BeforeSave = (_, _) => throw new UnauthorizedAccessException("cannot restore")
        };
        var telegram = new FakeTelegramClient
        {
            ResetImpl = _ => throw new IOException("reset failed")
        };
        var ex = await Assert.ThrowsAsync<TelegramApiCredentialsUpdateException>(() =>
            new DeleteTelegramApiCredentialsHandler(telegram, store, new TelegramOperationGate(), new FakeCredentialChangeJournal())
                .Handle(new DeleteTelegramApiCredentialsCommand(), default));
        Assert.False(ex.PreviousCredentialsRestored);
        Assert.IsType<AggregateException>(ex.InnerException);
    }

    [Theory]
    [InlineData("10", "0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("11", "0123456789abcdef0123456789abcdef", true)]
    [InlineData("bad", "0123456789abcdef0123456789abcdef", true)]
    [InlineData("10", "fedcba9876543210fedcba9876543210", true)]
    public async Task Compare_ReturnsWhetherCredentialsDiffer(string id, string hash, bool expected)
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(10, HashA) };
        var result = await new HaveTelegramApiCredentialsChangedHandler(store, new TelegramOperationGate())
            .Handle(new HaveTelegramApiCredentialsChangedQuery(id, hash), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public async Task Compare_FirstConfiguration_IsChanged()
    {
        var result = await new HaveTelegramApiCredentialsChangedHandler(new FakeCredentialsStore(), new TelegramOperationGate())
            .Handle(new HaveTelegramApiCredentialsChangedQuery("10", HashA), default);
        Assert.True(result.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_MatchesStore(bool configured)
    {
        var store = new FakeCredentialsStore
        {
            Value = configured ? new TelegramApiCredentials(10, HashA) : null
        };
        var result = await new GetTelegramApiStatusHandler(store, new TelegramOperationGate())
            .Handle(new GetTelegramApiStatusQuery(), default);
        Assert.Equal(configured, result.Value);
    }
}
