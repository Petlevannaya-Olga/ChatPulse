using System.IO;
using ChatPulse.Application.Features.Telegram.BeginLogin;
using ChatPulse.Application.Features.Telegram.ContinueLogin;
using ChatPulse.Application.Features.Telegram.ResendCode;
using ChatPulse.Application.Features.Telegram.RestoreSession;
using ChatPulse.Application.Features.Telegram.ResetSession;
using ChatPulse.Application.Features.Telegram.GetChats;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Shared;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramLoginHandlersTests
{
    private static readonly TelegramLoginProgress Waiting =
        new(TelegramLoginInputKind.VerificationCode, CodeDelivery: TelegramCodeDeliveryKind.TelegramApp);
    private static readonly TelegramLoginProgress Authorized =
        new(TelegramLoginInputKind.None, "Тестовый аккаунт", 12345);

    [Fact]
    public async Task Begin_PassesPhoneAndReturnsNextStep()
    {
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) => Task.FromResult(Waiting)
        };
        var result = await new BeginTelegramLoginHandler(client, new TelegramOperationGate())
            .Handle(new BeginTelegramLoginCommand("+79991234567"), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(Waiting, result.Value);
        Assert.Equal("+79991234567", client.SubmittedPhone);
        Assert.Equal(1, client.BeginCalls);
    }

    [Fact]
    public async Task Continue_PassesSecretAndReturnsAuthorizedUser()
    {
        var client = new FakeTelegramClient
        {
            ContinueImpl = (_, _) => Task.FromResult(Authorized)
        };
        var result = await new ContinueTelegramLoginHandler(client, new TelegramOperationGate())
            .Handle(new ContinueTelegramLoginCommand("secret"), default);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsAuthorized);
        Assert.Equal("secret", client.SubmittedConfirmation);
    }

    [Fact]
    public async Task Continue_CanRequireCloudPassword()
    {
        var client = new FakeTelegramClient
        {
            ContinueImpl = (_, _) => Task.FromResult(new TelegramLoginProgress(TelegramLoginInputKind.Password))
        };
        var result = await new ContinueTelegramLoginHandler(client, new TelegramOperationGate())
            .Handle(new ContinueTelegramLoginCommand("12345"), default);
        Assert.Equal(TelegramLoginInputKind.Password, result.Value.RequiredInput);
        Assert.False(result.Value.IsAuthorized);
    }

    [Theory]
    [InlineData(TelegramLoginInputKind.VerificationCode)]
    [InlineData(TelegramLoginInputKind.Email)]
    [InlineData(TelegramLoginInputKind.EmailVerificationCode)]
    [InlineData(TelegramLoginInputKind.Password)]
    public void LoginProgress_RequestedInputIsNotAuthorized(TelegramLoginInputKind step)
        => Assert.False(new TelegramLoginProgress(step).IsAuthorized);

    [Fact]
    public void LoginProgress_NoneWithoutUserId_IsNotAuthorized()
        => Assert.False(new TelegramLoginProgress(TelegramLoginInputKind.None).IsAuthorized);

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void LoginProgress_InvalidUserId_IsNotAuthorized(long id)
        => Assert.False(new TelegramLoginProgress(TelegramLoginInputKind.None, UserId: id).IsAuthorized);

    [Fact]
    public async Task Restore_ReturnsSavedSessionWithoutRequestingCode()
    {
        var client = new FakeTelegramClient { RestoreImpl = _ => Task.FromResult(Authorized) };
        var result = await new RestoreTelegramSessionHandler(client, new TelegramOperationGate())
            .Handle(new RestoreTelegramSessionQuery(), default);
        Assert.True(result.Value.IsAuthorized);
        Assert.Equal(1, client.RestoreCalls);
        Assert.Equal(0, client.BeginCalls);
    }

    [Fact]
    public async Task Restore_NoSession_ReturnsNotAuthorized()
    {
        var result = await new RestoreTelegramSessionHandler(new FakeTelegramClient(), new TelegramOperationGate())
            .Handle(new RestoreTelegramSessionQuery(), default);
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsAuthorized);
    }

    [Fact]
    public async Task Resend_ReturnsNewDeliveryStep()
    {
        var next = new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode,
            CodeDelivery: TelegramCodeDeliveryKind.Sms,
            HasResendOption: false);
        var client = new FakeTelegramClient { ResendImpl = _ => Task.FromResult(next) };
        var result = await new ResendTelegramCodeHandler(client, new TelegramOperationGate())
            .Handle(new ResendTelegramCodeCommand(), default);
        Assert.Equal(TelegramCodeDeliveryKind.Sms, result.Value.CodeDelivery);
        Assert.Equal(1, client.ResendCalls);
    }

    [Fact]
    public async Task Reset_InvokesLocalResetWithoutTouchingKeyStore()
    {
        var client = new FakeTelegramClient();
        var result = await new ResetTelegramSessionHandler(client, new TelegramOperationGate(), new FakeCredentialChangeJournal())
            .Handle(new ResetTelegramSessionCommand(), default);
        Assert.True(result.Value);
        Assert.Equal(1, client.ResetCalls);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("continue")]
    [InlineData("resend")]
    [InlineData("restore")]
    [InlineData("chats")]
    public async Task ExpectedTelegramFailure_IsMappedToResult(string operation)
    {
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) => throw new TelegramAuthenticationException("test.error", "Expected failure"),
            ContinueImpl = (_, _) => throw new TelegramAuthenticationException("test.error", "Expected failure"),
            RestoreImpl = _ => throw new TelegramAuthenticationException("test.error", "Expected failure"),
            ResendImpl = _ => throw new TelegramAuthenticationException("test.error", "Expected failure"),
            ChatsImpl = _ => throw new TelegramAuthenticationException("test.error", "Expected failure")
        };
        Error error = operation switch
        {
            "begin" => (await new BeginTelegramLoginHandler(client, new TelegramOperationGate()).Handle(new BeginTelegramLoginCommand("+79991234567"), default)).Error,
            "continue" => (await new ContinueTelegramLoginHandler(client, new TelegramOperationGate()).Handle(new ContinueTelegramLoginCommand("1234"), default)).Error,
            "resend" => (await new ResendTelegramCodeHandler(client, new TelegramOperationGate()).Handle(new ResendTelegramCodeCommand(), default)).Error,
            "restore" => (await new RestoreTelegramSessionHandler(client, new TelegramOperationGate()).Handle(new RestoreTelegramSessionQuery(), default)).Error,
            "chats" => (await new GetTelegramChatsHandler(client, new TelegramOperationGate()).Handle(new GetTelegramChatsQuery(), default)).Error,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        Assert.Equal("test.error", error.Code);
        Assert.Equal(ErrorType.SERVICE_UNAVAILABLE, error.Type);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("continue")]
    [InlineData("resend")]
    [InlineData("restore")]
    [InlineData("chats")]
    public async Task CanceledOperation_IsNotReportedAsLoginFailure(string operation)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, token) => Task.FromCanceled<TelegramLoginProgress>(token),
            ContinueImpl = (_, token) => Task.FromCanceled<TelegramLoginProgress>(token),
            RestoreImpl = token => Task.FromCanceled<TelegramLoginProgress>(token),
            ResendImpl = token => Task.FromCanceled<TelegramLoginProgress>(token),
            ChatsImpl = token => Task.FromCanceled<TelegramChatsSnapshot>(token)
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(operation switch
        {
            "begin" => () => new BeginTelegramLoginHandler(client, new TelegramOperationGate()).Handle(new BeginTelegramLoginCommand("+79991234567"), cts.Token),
            "continue" => () => new ContinueTelegramLoginHandler(client, new TelegramOperationGate()).Handle(new ContinueTelegramLoginCommand("1234"), cts.Token),
            "resend" => () => new ResendTelegramCodeHandler(client, new TelegramOperationGate()).Handle(new ResendTelegramCodeCommand(), cts.Token),
            "restore" => () => new RestoreTelegramSessionHandler(client, new TelegramOperationGate()).Handle(new RestoreTelegramSessionQuery(), cts.Token),
            "chats" => () => new GetTelegramChatsHandler(client, new TelegramOperationGate()).Handle(new GetTelegramChatsQuery(), cts.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });
    }

    [Fact]
    public async Task UnexpectedException_IsNotMaskedByHandler()
    {
        var client = new FakeTelegramClient { BeginImpl = (_, _) => throw new IOException("unexpected") };
        await Assert.ThrowsAsync<IOException>(() =>
            new BeginTelegramLoginHandler(client, new TelegramOperationGate()).Handle(new BeginTelegramLoginCommand("+79991234567"), default));
    }

    [Fact]
    public async Task ResetFailure_BubblesUp()
    {
        var client = new FakeTelegramClient { ResetImpl = _ => throw new UnauthorizedAccessException() };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new ResetTelegramSessionHandler(client, new TelegramOperationGate(), new FakeCredentialChangeJournal()).Handle(new ResetTelegramSessionCommand(), default));
    }
}
