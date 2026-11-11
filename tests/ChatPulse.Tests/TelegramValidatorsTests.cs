using ChatPulse.Application.Features.Telegram.ApiCredentials.Save;
using ChatPulse.Application.Features.Telegram.BeginLogin;
using ChatPulse.Application.Features.Telegram.ContinueLogin;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramValidatorsTests
{
    private const string ValidHash = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("123456789")]
    [InlineData("2147483647")]
    public void ApiId_ValidValues_Accepted(string value)
    {
        var result = new SaveTelegramApiCredentialsValidator().Validate(
            new SaveTelegramApiCredentialsCommand(value, ValidHash));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+12")]
    [InlineData(" 12")]
    [InlineData("12 ")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("abc")]
    [InlineData("１２")]
    public void ApiId_InvalidValues_Rejected(string value)
    {
        var result = new SaveTelegramApiCredentialsValidator().Validate(
            new SaveTelegramApiCredentialsCommand(value, ValidHash));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "ApiId");
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void ApiHash_ValidHex32_Accepted(string value)
    {
        var result = new SaveTelegramApiCredentialsValidator().Validate(
            new SaveTelegramApiCredentialsCommand("123", value));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcde ")]
    [InlineData("0123456789abcdef0123456789abcdef\n")]
    public void ApiHash_InvalidValues_Rejected(string value)
    {
        var result = new SaveTelegramApiCredentialsValidator().Validate(
            new SaveTelegramApiCredentialsCommand("123", value));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "ApiHash");
    }

    [Theory]
    [InlineData("+79991234567")]
    [InlineData("+70000000000")]
    public void Phone_ValidRuFormat_Accepted(string phone)
        => Assert.True(new BeginTelegramLoginValidator().Validate(new BeginTelegramLoginCommand(phone)).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("79991234567")]
    [InlineData("89991234567")]
    [InlineData("+7999123456")]
    [InlineData("+799912345678")]
    [InlineData("+7(999)1234567")]
    [InlineData("+7abcdefghij")]
    [InlineData("+1 9991234567")]
    [InlineData("+7９９９１２３４５６７")]
    [InlineData("+79991234567\n")]
    public void Phone_InvalidRuFormat_Rejected(string phone)
        => Assert.False(new BeginTelegramLoginValidator().Validate(new BeginTelegramLoginCommand(phone)).IsValid);

    [Fact]
    public void Confirmation_Required()
        => Assert.False(new ContinueTelegramLoginValidator()
            .Validate(new ContinueTelegramLoginCommand("")).IsValid);

    [Theory]
    [InlineData("12345")]
    [InlineData("one-time-code")]
    [InlineData("пароль с пробелами")]
    public void Confirmation_MultipleKindsOfInput_AreAccepted(string input)
        => Assert.True(new ContinueTelegramLoginValidator()
            .Validate(new ContinueTelegramLoginCommand(input)).IsValid);

    [Fact]
    public void Confirmation_Over256Chars_IsRejected()
        => Assert.False(new ContinueTelegramLoginValidator()
            .Validate(new ContinueTelegramLoginCommand(new string('x', 257))).IsValid);

    [Fact]
    public void Confirmation_Exactly256Chars_IsAccepted()
        => Assert.True(new ContinueTelegramLoginValidator()
            .Validate(new ContinueTelegramLoginCommand(new string('x', 256))).IsValid);
}
