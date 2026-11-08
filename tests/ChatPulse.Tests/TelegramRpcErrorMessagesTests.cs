using ChatPulse.Infrastructure.Telegram;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramRpcErrorMessagesTests
{
    [Theory]
    [InlineData("API_ID_INVALID", "API ID")]
    [InlineData("PHONE_NUMBER_INVALID", "номер")]
    [InlineData("PHONE_NUMBER_BANNED", "поддержку")]
    [InlineData("PHONE_NUMBER_FLOOD", "частоту")]
    [InlineData("FLOOD_WAIT_120", "ограничил")]
    public void Begin_ProvidesActionableSafeMessage(string rpc, string expectedWord)
        => Assert.Contains(expectedWord, TelegramRpcErrorMessages.ForBegin(rpc));

    [Theory]
    [InlineData("PHONE_CODE_INVALID", "Неверный код")]
    [InlineData("PHONE_CODE_EXPIRED", "истёк")]
    [InlineData("PHONE_CODE_HASH_INVALID", "заново")]
    [InlineData("PASSWORD_HASH_INVALID", "пароль")]
    [InlineData("EMAIL_TOKEN_INVALID", "электронной почты")]
    [InlineData("CODE_INVALID", "электронной почты")]
    [InlineData("FLOOD_WAIT_60", "ограничил")]
    public void Confirm_ProvidesActionableSafeMessage(string rpc, string expectedWord)
        => Assert.Contains(expectedWord, TelegramRpcErrorMessages.ForConfirmation(rpc, 400));

    [Theory]
    [InlineData("SEND_CODE_UNAVAILABLE", "не разрешает")]
    [InlineData("PHONE_CODE_EXPIRED", "заново")]
    [InlineData("PHONE_NUMBER_FLOOD", "частоту")]
    [InlineData("FLOOD_WAIT_60", "ограничил")]
    public void Resend_ProvidesActionableSafeMessage(string rpc, string expectedWord)
        => Assert.Contains(expectedWord, TelegramRpcErrorMessages.ForResend(rpc, 400));

    [Fact]
    public void UnexpectedRpcText_NeverReachesUserMessage()
    {
        const string risky = "some_private_value_0123456789";
        Assert.DoesNotContain(risky, TelegramRpcErrorMessages.ForBegin(risky));
        Assert.DoesNotContain(risky, TelegramRpcErrorMessages.ForConfirmation(risky, 400));
        Assert.DoesNotContain(risky, TelegramRpcErrorMessages.ForResend(risky, 400));
    }
}
