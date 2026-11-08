using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Tests;

internal sealed class FakeCredentialsStore : ITelegramApiCredentialsStore
{
    public TelegramApiCredentials? Value { get; set; }
    public int LoadCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public int DeleteCalls { get; private set; }
    public Func<int, TelegramApiCredentials, Task>? BeforeSave { get; set; }
    public Func<int, Task>? BeforeDelete { get; set; }
    public Func<Task>? BeforeLoad { get; set; }
    public Func<Task>? BeforeIsConfigured { get; set; }

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeIsConfigured is not null) await BeforeIsConfigured();
        return Value is not null;
    }

    public async Task<TelegramApiCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadCalls++;
        if (BeforeLoad is not null) await BeforeLoad();
        return Value;
    }

    public async Task SaveAsync(TelegramApiCredentials credentials, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCalls++;
        if (BeforeSave is not null) await BeforeSave(SaveCalls, credentials);
        Value = credentials;
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCalls++;
        if (BeforeDelete is not null) await BeforeDelete(DeleteCalls);
        Value = null;
    }
}

internal sealed class FakeTelegramClient : ITelegramAuthenticationClient
{
    public int ResetCalls { get; private set; }
    public int CancelCalls { get; private set; }
    public Func<CancellationToken, Task>? CancelImpl { get; set; }
    public int BeginCalls { get; private set; }
    public int ContinueCalls { get; private set; }
    public int RestoreCalls { get; private set; }
    public int ResendCalls { get; private set; }
    public int GetChatsCalls { get; private set; }
    public long CurrentTelegramUserId { get; set; } = 123;
    public Func<CancellationToken, Task<long>>? GetUserIdImpl { get; set; }
    public string? SubmittedPhone { get; private set; }
    public string? SubmittedConfirmation { get; private set; }

    public Func<CancellationToken, Task>? ResetImpl { get; set; }
    public Func<string, CancellationToken, Task<TelegramLoginProgress>>? BeginImpl { get; set; }
    public Func<string, CancellationToken, Task<TelegramLoginProgress>>? ContinueImpl { get; set; }
    public Func<CancellationToken, Task<TelegramLoginProgress>>? RestoreImpl { get; set; }
    public Func<CancellationToken, Task<TelegramLoginProgress>>? ResendImpl { get; set; }
    public Func<CancellationToken, Task<TelegramChatsSnapshot>>? ChatsImpl { get; set; }

    public Task CancelLoginAsync(CancellationToken cancellationToken)
    {
        CancelCalls++;
        return CancelImpl?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task ResetSessionAsync(CancellationToken cancellationToken)
    {
        ResetCalls++;
        return ResetImpl?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task<TelegramLoginProgress> BeginLoginAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        BeginCalls++;
        SubmittedPhone = phoneNumber;
        return BeginImpl?.Invoke(phoneNumber, cancellationToken)
            ?? Task.FromResult(new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode));
    }

    public Task<TelegramLoginProgress> ContinueLoginAsync(string input, CancellationToken cancellationToken)
    {
        ContinueCalls++;
        SubmittedConfirmation = input;
        return ContinueImpl?.Invoke(input, cancellationToken)
            ?? Task.FromResult(new TelegramLoginProgress(TelegramLoginInputKind.None, "Test", 123));
    }

    public Task<TelegramLoginProgress> RestoreSessionAsync(CancellationToken cancellationToken)
    {
        RestoreCalls++;
        return RestoreImpl?.Invoke(cancellationToken)
            ?? Task.FromResult(new TelegramLoginProgress(TelegramLoginInputKind.None));
    }

    public Task<TelegramLoginProgress> ResendCodeAsync(CancellationToken cancellationToken)
    {
        ResendCalls++;
        return ResendImpl?.Invoke(cancellationToken)
            ?? Task.FromResult(new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode));
    }

    public Task<TelegramChatsSnapshot> GetChatsAsync(CancellationToken cancellationToken)
    {
        GetChatsCalls++;
        return ChatsImpl?.Invoke(cancellationToken)
            ?? Task.FromResult(new TelegramChatsSnapshot(CurrentTelegramUserId, []));
    }

    public Task<long> GetAuthorizedUserIdAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GetUserIdImpl is not null) return GetUserIdImpl(cancellationToken);
        if (CurrentTelegramUserId <= 0)
            throw new TelegramAuthenticationException("telegram.not_authorized", "Telegram не подключён.");
        return Task.FromResult(CurrentTelegramUserId);
    }
}


internal sealed class FakeCredentialChangeJournal : ITelegramCredentialChangeJournal
{
    public bool IsPending { get; private set; }
    public int MarkCalls { get; private set; }
    public int ClearCalls { get; private set; }
    public Func<CancellationToken, Task>? BeforeMark { get; set; }
    public Func<CancellationToken, Task>? BeforeClear { get; set; }

    public async Task MarkPendingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MarkCalls++;
        if (BeforeMark is not null) await BeforeMark(cancellationToken);
        IsPending = true;
    }

    public Task<bool> IsPendingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsPending);
    }

    public async Task ClearPendingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearCalls++;
        if (BeforeClear is not null) await BeforeClear(cancellationToken);
        IsPending = false;
    }
}
