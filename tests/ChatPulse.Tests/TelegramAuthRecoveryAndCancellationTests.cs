using System.IO;
using ChatPulse.Application.Features.Telegram.BeginLogin;
using ChatPulse.Application.Features.Telegram.CancelLogin;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.ViewModels;
using ChatPulse.Infrastructure.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramAuthRecoveryAndCancellationTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef";
    private static readonly TelegramLoginProgress Authorized =
        new(TelegramLoginInputKind.None, "Тестовый Пользователь", 456);

    [Fact]
    public async Task CancelPendingLogin_UsesOperationGateAndRetainsKeyStore()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, Hash) };
        var client = new FakeTelegramClient();
        var journal = new FakeCredentialChangeJournal();
        var result = await new CancelTelegramLoginHandler(client, new TelegramOperationGate(), journal)
            .Handle(new CancelTelegramLoginCommand(), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, client.CancelCalls);
        Assert.Equal(0, client.ResetCalls);
        Assert.Equal(1, journal.MarkCalls);
        Assert.Equal(1, journal.ClearCalls);
        Assert.False(journal.IsPending);
        Assert.NotNull(store.Value);
    }

    [Fact]
    public async Task CancelWithoutPendingLogin_DoesNotLeaveMarkerOrInvalidateExistingSession()
    {
        var client = new FakeTelegramClient
        {
            CancelImpl = _ => throw new TelegramAuthenticationException(
                "telegram.login_not_pending", "Нет незавершённого входа для отмены.")
        };
        var journal = new FakeCredentialChangeJournal();
        var result = await new CancelTelegramLoginHandler(client, new TelegramOperationGate(), journal)
            .Handle(new CancelTelegramLoginCommand(), default);

        Assert.True(result.IsFailure);
        Assert.Equal("telegram.login_not_pending", result.Error.Code);
        Assert.Equal(1, journal.MarkCalls);
        Assert.Equal(1, journal.ClearCalls);
        Assert.False(journal.IsPending);
        Assert.Equal(0, client.ResetCalls);
    }

    [Fact]
    public async Task CancelWithoutPendingLogin_PreservesEarlierRecoveryMarker()
    {
        var client = new FakeTelegramClient
        {
            CancelImpl = _ => throw new TelegramAuthenticationException(
                "telegram.login_not_pending", "Нет незавершённого входа для отмены.")
        };
        var journal = new FakeCredentialChangeJournal();
        await journal.MarkPendingAsync(default); // Interrupted change from an earlier operation.

        var result = await new CancelTelegramLoginHandler(client, new TelegramOperationGate(), journal)
            .Handle(new CancelTelegramLoginCommand(), default);

        Assert.True(result.IsFailure);
        Assert.True(journal.IsPending);
        Assert.Equal(1, journal.MarkCalls);
        Assert.Equal(0, journal.ClearCalls);
        Assert.Equal(0, client.ResetCalls);
    }

    [Fact]
    public async Task CancelWithoutPendingLogin_DoesNotStrandViewModelOnConfirmation()
    {
        var client = new FakeTelegramClient
        {
            CancelImpl = _ => throw new TelegramAuthenticationException(
                "telegram.login_not_pending", "Вход уже завершён.")
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();
        Assert.True(vm.IsAwaitingInput);

        Assert.False(await vm.CancelLoginAsync());
        Assert.False(vm.IsAwaitingInput);
        Assert.False(vm.IsSessionRecoveryNeeded);
        Assert.True(vm.CanStart);
        Assert.True(vm.HasError);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("continue")]
    [InlineData("resend")]
    public async Task LoginRpcAuthorizationLoss_RequiresCleanupInsteadOfLeavingPendingInput(string phase)
    {
        var error = new TelegramAuthenticationException("telegram.session_revoked", "Сессия отозвана.");
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) => phase == "begin" ? throw error
                : Task.FromResult(new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode,
                    HasResendOption: true)),
            ContinueImpl = (_, _) => throw error,
            ResendImpl = _ => throw error
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();
        if (phase == "continue") await vm.ConfirmAsync("12345");
        if (phase == "resend") await vm.ResendCodeAsync();

        Assert.False(vm.IsAuthorized);
        Assert.False(vm.IsAwaitingInput);
        Assert.True(vm.IsSessionRecoveryNeeded);
        Assert.True(vm.CanResetTelegramSession);
    }

    [Theory]
    [InlineData("telegram.account_missing")]
    [InlineData("telegram.unsupported_step")]
    public async Task UnsupportedConfirmationStep_DoesNotLeaveOldCodeInputOnScreen(string code)
    {
        var client = new FakeTelegramClient
        {
            ContinueImpl = (_, _) => throw new TelegramAuthenticationException(code,
                "Telegram запросил неподдерживаемый шаг входа.")
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();
        Assert.True(vm.IsAwaitingInput);

        await vm.ConfirmAsync("12345");

        Assert.False(vm.IsAwaitingInput);
        Assert.False(vm.IsAuthorized);
        Assert.False(vm.IsSessionRecoveryNeeded);
        Assert.False(vm.IsSessionRetryNeeded);
        Assert.True(vm.CanStart);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task UnsupportedConfirmationStep_CleanupFailure_OffersLocalSessionReset()
    {
        var client = new FakeTelegramClient
        {
            ContinueImpl = (_, _) => throw new TelegramAuthenticationException(
                "telegram.session_cleanup_failed", "Не удалось очистить незавершённую сессию.")
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();

        await vm.ConfirmAsync("12345");

        Assert.False(vm.IsAwaitingInput);
        Assert.True(vm.IsSessionRecoveryNeeded);
        Assert.True(vm.CanResetTelegramSession);
    }

    [Fact]
    public async Task CancelFailure_KeepsRecoveryMarkerForNextLaunch()
    {
        var client = new FakeTelegramClient { CancelImpl = _ => throw new IOException("disk full") };
        var journal = new FakeCredentialChangeJournal();
        await Assert.ThrowsAsync<IOException>(() =>
            new CancelTelegramLoginHandler(client, new TelegramOperationGate(), journal)
                .Handle(new CancelTelegramLoginCommand(), default));
        Assert.True(journal.IsPending);
        Assert.Equal(0, journal.ClearCalls);
    }

    [Fact]
    public async Task Cancel_WaitsForInFlightTelegramRequest()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<TelegramLoginProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) =>
            {
                started.TrySetResult(true);
                return finish.Task;
            }
        };
        using var gate = new TelegramOperationGate();
        var journal = new FakeCredentialChangeJournal();
        var begin = new BeginTelegramLoginHandler(client, gate)
            .Handle(new BeginTelegramLoginCommand("+79991234567"), default);
        await started.Task;
        var cancel = new CancelTelegramLoginHandler(client, gate, journal)
            .Handle(new CancelTelegramLoginCommand(), default);
        Assert.Equal(0, client.CancelCalls);
        Assert.Equal(0, journal.MarkCalls);

        finish.SetResult(new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode));
        await begin;
        await cancel;
        Assert.Equal(1, client.CancelCalls);
    }

    [Fact]
    public async Task Cancel_NoPendingStep_DoesNotSendRequestOrShowRecovery()
    {
        using var provider = CreateVmProvider(new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        Assert.False(vm.CanCancelLogin);
        Assert.False(await vm.CancelLoginAsync());
    }

    [Fact]
    public async Task Cancel_AfterCodeRequest_ReturnsToPhoneAndKeepsApiKeys()
    {
        var client = new FakeTelegramClient();
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, Hash) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();
        Assert.True(vm.IsAwaitingInput);
        Assert.True(vm.CanCancelLogin);
        Assert.True(await vm.CancelLoginAsync());
        Assert.False(vm.IsAwaitingInput);
        Assert.False(vm.IsAuthorized);
        Assert.True(vm.IsApiConfigured);
        Assert.False(vm.IsSessionRecoveryNeeded);
        Assert.Equal(1, client.CancelCalls);
        Assert.Equal(Hash, store.Value!.ApiHash);
    }

    [Fact]
    public async Task Cancel_WhenFileCannotBeRemoved_OffersRecoveryInsteadOfStaleCode()
    {
        var client = new FakeTelegramClient { CancelImpl = _ => throw new IOException("access denied") };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();
        Assert.False(await vm.CancelLoginAsync());
        Assert.False(vm.IsAwaitingInput);
        Assert.True(vm.IsSessionRecoveryNeeded);
        Assert.True(vm.CanResetTelegramSession);
    }

    [Fact]
    public async Task SessionLostEvent_RemovesConnectedBadgeAndOffersCleanup()
    {
        using var provider = CreateVmProvider(new FakeTelegramClient
        {
            RestoreImpl = _ => Task.FromResult(Authorized)
        });
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.True(vm.IsAuthorized);

        provider.GetRequiredService<TelegramAuthorizationEvents>().ReportAuthorizationLost();
        Assert.False(vm.IsAuthorized);
        Assert.True(vm.IsSessionRecoveryNeeded);
        Assert.True(vm.CanResetTelegramSession);
    }

    [Fact]
    public async Task VerificationRequiredEvent_StopsTrustingStaleBadge_WithoutRequestingDestructiveCleanup()
    {
        using var provider = CreateVmProvider(new FakeTelegramClient
        {
            RestoreImpl = _ => Task.FromResult(Authorized)
        });
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.True(vm.IsAuthorized);

        provider.GetRequiredService<TelegramAuthorizationEvents>().ReportVerificationRequired();
        Assert.False(vm.IsAuthorized);
        Assert.True(vm.IsSessionRetryNeeded);
        Assert.True(vm.CanRetrySession);
        Assert.False(vm.IsSessionRecoveryNeeded);
    }

    [Fact]
    public async Task TransientRestoreFailure_CanRetryAndRecoverOriginalAccount()
    {
        var attempt = 0;
        var client = new FakeTelegramClient
        {
            RestoreImpl = _ => ++attempt == 1
                ? throw new TelegramAuthenticationException("telegram.session_restore_failed", "Network unavailable")
                : Task.FromResult(Authorized)
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.RestoreSessionAsync();
        Assert.True(vm.IsSessionRetryNeeded);
        Assert.False(vm.CanStart); // Запрос нового кода заблокирован до проверки старой сессии.

        await vm.RestoreSessionAsync();
        Assert.True(vm.IsAuthorized);
        Assert.False(vm.IsSessionRetryNeeded);
        Assert.False(vm.IsSessionRecoveryNeeded);
        Assert.Equal(2, client.RestoreCalls);
        Assert.Equal(0, client.BeginCalls);
    }

    [Fact]
    public async Task RetryWithoutAuthorizedSession_ReturnsToPhoneLoginWithExplanation()
    {
        var attempt = 0;
        var client = new FakeTelegramClient
        {
            RestoreImpl = _ => ++attempt == 1
                ? throw new TelegramAuthenticationException("telegram.session_restore_failed", "Network unavailable")
                : Task.FromResult(new TelegramLoginProgress(TelegramLoginInputKind.None))
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();

        await vm.RestoreSessionAsync();
        Assert.True(vm.CanRetrySession);
        await vm.RestoreSessionAsync();

        Assert.Equal(2, client.RestoreCalls);
        Assert.False(vm.IsAuthorized);
        Assert.False(vm.IsSessionRetryNeeded);
        Assert.False(vm.IsSessionRecoveryNeeded);
        Assert.Contains("Войдите", vm.Error);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CorruptSession_RestoreOffersCleanup()
    {
        var client = new FakeTelegramClient
        {
            RestoreImpl = _ => throw new TelegramAuthenticationException("telegram.session_corrupted", "Corrupted session")
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.True(vm.CanResetTelegramSession);
        Assert.False(vm.IsAuthorized);
    }

    [Fact]
    public async Task NetworkError_DoesNotOfferDestructiveRecovery()
    {
        var client = new FakeTelegramClient
        {
            RestoreImpl = _ => throw new TelegramAuthenticationException("telegram.session_restore_failed", "Network error")
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.False(vm.CanResetTelegramSession);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task TimeoutDuringCodeEntry_ClearsObsoleteStep()
    {
        var client = new FakeTelegramClient
        {
            ContinueImpl = (_, _) => throw new TelegramAuthenticationException("telegram.request_timeout", "Timed out")
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();
        Assert.True(vm.IsAwaitingInput);
        await vm.ConfirmAsync("12345");
        Assert.False(vm.IsAwaitingInput);
        Assert.True(vm.HasError);
        Assert.False(vm.IsSessionRecoveryNeeded);
    }

    [Theory]
    [InlineData(401, "AUTH_KEY_UNREGISTERED", true)]
    [InlineData(401, "OTHER", true)]
    [InlineData(400, "SESSION_REVOKED", true)]
    [InlineData(400, "AUTH_KEY_UNREGISTERED", true)]
    [InlineData(406, "AUTH_KEY_DUPLICATED", true)]
    [InlineData(400, "AUTH_KEY_DUPLICATED", true)]
    [InlineData(401, "SESSION_EXPIRED", true)]
    [InlineData(400, "SESSION_EXPIRED", true)]
    [InlineData(400, "AUTH_KEY_INVALID", true)]
    [InlineData(406, "OTHER", false)]
    [InlineData(420, "FLOOD_WAIT_60", false)]
    [InlineData(500, "SERVER_ERROR", false)]
    public void RpcError_CanDistinguishLostSessionFromNetworkProblems(
        int code, string message, bool expected)
        => Assert.Equal(expected, TelegramAuthorizationFailure.IsLost(code, message));

    [Fact]
    public async Task RemoteWait_CompletedRequest_DoesNotDetachClient()
    {
        var detached = false;
        var result = await TelegramRemoteAwait.AwaitAsync(
            Task.FromResult(42), TimeSpan.FromSeconds(1), default,
            () => { detached = true; return Task.CompletedTask; });
        Assert.Equal(42, result);
        Assert.False(detached);
    }

    [Fact]
    public async Task RemoteWait_Timeout_DetachesBeforeReturningToCaller()
    {
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detached = false;
        var error = await Assert.ThrowsAsync<TelegramAuthenticationException>(() =>
            TelegramRemoteAwait.AwaitAsync(pending.Task, TimeSpan.FromMilliseconds(30), default,
                () => { detached = true; return Task.CompletedTask; }));
        Assert.True(detached);
        Assert.Equal("telegram.request_timeout", error.Code);
        pending.SetResult(1);
    }

    [Fact]
    public async Task RemoteWait_Cancellation_DetachesOldClient()
    {
        using var cts = new CancellationTokenSource();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detached = false;
        var waiting = TelegramRemoteAwait.AwaitAsync(pending.Task, TimeSpan.FromMinutes(1), cts.Token,
            () => { detached = true; return Task.CompletedTask; });
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(detached);
        pending.SetResult(1);
    }

    [Fact]
    public async Task RemoteWait_RpcFailure_IsNotWronglyClassifiedAsTimeout()
    {
        var detached = false;
        await Assert.ThrowsAsync<IOException>(() => TelegramRemoteAwait.AwaitAsync(
            Task.FromException<int>(new IOException("Remote failure")), TimeSpan.FromSeconds(1), default,
            () => { detached = true; return Task.CompletedTask; }));
        Assert.False(detached);
    }


    [Fact]
    public async Task RemoteWait_NetworkDisconnection_DetachesAndReportsActionableError()
    {
        var request = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lostConnection = new CancellationTokenSource();
        var detachCalls = 0;
        var waiting = TelegramRemoteAwait.AwaitAsync(
            request.Task, TimeSpan.FromMinutes(1), CancellationToken.None,
            () => { detachCalls++; return Task.CompletedTask; }, lostConnection.Token);

        lostConnection.Cancel();

        var exception = await Assert.ThrowsAsync<TelegramAuthenticationException>(() => waiting);
        Assert.Equal("telegram.network_unavailable", exception.Code);
        Assert.Contains("интернетом", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("код", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, detachCalls);
        request.TrySetResult(42); // The late result must not authorize the old client.
    }

    [Fact]
    public async Task RemoteWait_ExternalCancellation_IsNotMisreportedAsNetworkLoss()
    {
        var request = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var external = new CancellationTokenSource();
        using var lostConnection = new CancellationTokenSource();
        var detachCalls = 0;
        var waiting = TelegramRemoteAwait.AwaitAsync(
            request.Task, TimeSpan.FromMinutes(1), external.Token,
            () => { detachCalls++; return Task.CompletedTask; }, lostConnection.Token);

        external.Cancel();
        lostConnection.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, detachCalls);
        request.TrySetResult(42);
    }

    [Fact]
    public async Task BeginLogin_NetworkError_IsDisplayedAndRequiresSessionRecheck()
    {
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) => throw new TelegramAuthenticationException(
                "telegram.network_unavailable",
                "Соединение с интернетом прервано. Проверьте подключение и повторите попытку.")
        };
        using var provider = CreateVmProvider(client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";

        await vm.BeginAsync();

        Assert.False(vm.IsBusy);
        Assert.True(vm.HasError);
        Assert.Contains("интернетом", vm.Error);
        Assert.True(vm.IsSessionRetryNeeded);
        Assert.False(vm.IsSessionRecoveryNeeded);
        Assert.False(vm.IsAuthorized);
        Assert.False(vm.IsAwaitingInput);
        Assert.Equal("+79991234567", vm.PhoneNumber);
        Assert.False(vm.CanStart); // Prevent duplicate code requests while outcome is uncertain.
        Assert.True(vm.CanRetrySession);

        await vm.RestoreSessionAsync(); // No saved session: verification re-enables phone input.
        Assert.False(vm.IsSessionRetryNeeded);
        Assert.True(vm.CanStart);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransportErrorClassifier_DistinguishesNetworkAndLocalFileIOException(bool transport)
    {
        Exception error = transport
            ? new IOException("network stream", new System.Net.Sockets.SocketException())
            : new IOException("local session write failed");
        Assert.Equal(transport, TelegramNetworkErrors.IsTransportFailure(error));
    }

    private static ServiceProvider CreateVmProvider(FakeTelegramClient client)
        => TelegramValidationPipelineTests.BuildProvider(
            new FakeCredentialsStore { Value = new TelegramApiCredentials(123, Hash) }, client);
}
