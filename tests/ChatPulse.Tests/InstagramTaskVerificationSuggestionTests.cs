using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramTaskVerificationSuggestionTests
{
    [Fact]
    public void SuggestChecks_RecognizesCommentWithDescriptionFromTelegramTask()
    {
        var task = """
            VIP basic lite

            ♡ подписка
            ♡ лайк
            ♡ комментарий От души

            ♡ просмотр и реакция на сторис
            ♡ репост
            VIP lite
            Контролируется администрацией чата ОРИОН
            """;

        var checks = InstagramTaskVerificationRules.SuggestChecks(task);
        Assert.True(checks.Like);
        Assert.True(checks.Comment);
        Assert.False(checks.Save);
        Assert.True(checks.Repost);
    }

    [Fact]
    public void SuggestChecks_RecognizesTypoInOriginalTelegramTask()
    {
        var task = """
            https://www.instagram.com/reel/DfT76Gxugio/?gwpx=MW5mc2RhM3BzdGV5cg==

            https://www.instagram.com/p/DfT76Gxugio/?gwpx=MW5mc2RhM3BzdGV5cg==

            🤍подписка
            🤍лайк
            🤍коментарий От души

            🤍 просмотр и реакция на сторис
            🤍 репост

            💫 VIP lite 💫

            🌌 Контролируется администрацией чата ОРИОН
            """;

        var checks = InstagramTaskVerificationRules.SuggestChecks(task);
        Assert.True(checks.Like);
        Assert.True(checks.Comment);
        Assert.False(checks.Save);
        Assert.True(checks.Repost);
    }

    [Theory]
    [InlineData("коментарий не нужен")]
    [InlineData("Без коментария")]
    public void SuggestChecks_DoesNotSelectMisspelledExcludedComments(string task)
        => Assert.False(InstagramTaskVerificationRules.SuggestChecks(task).Comment);

    [Theory]
    [InlineData("♡ коммен\u200bтарий От души")]
    [InlineData("♡ коммeнтарий От души")]
    [InlineData("♡ комментaрий От души")]
    [InlineData("♡ коммен\u2060тарий От души")]
    [InlineData("♡ комментарий От души")]
    public void SuggestChecks_RecognizesCommonUnicodeVariations(string task)
        => Assert.True(InstagramTaskVerificationRules.SuggestChecks(task).Comment);

    [Theory]
    [InlineData("Комментарий не нужен")]
    [InlineData("Без комментария")]
    [InlineData("Лайк, комментарий не требуется")]
    public void SuggestChecks_DoesNotSelectExplicitlyExcludedComments(string task)
        => Assert.False(InstagramTaskVerificationRules.SuggestChecks(task).Comment);

    [Theory]
    [InlineData("")]
    [InlineData("Только репост")]
    [InlineData("Комментарий по желанию")]
    [InlineData("Сохранение")]
    public void SuggestChecks_AlwaysRequiresLike(string task)
        => Assert.True(InstagramTaskVerificationRules.SuggestChecks(task).Like);

    [Theory]
    [InlineData("🤍 комментарий по желанию")]
    [InlineData("🤍 коментарий по желанию")]
    [InlineData("Комментарий От души — по желанию")]
    [InlineData("По желанию: комментарий")]
    [InlineData("Комментарий необязателен")]
    [InlineData("Комментарий не обязателен")]
    [InlineData("Комментарий (по желанию)\nРепост")]
    public void SuggestChecks_DoesNotRequireOptionalComment(string task)
    {
        var checks = InstagramTaskVerificationRules.SuggestChecks(task);
        Assert.True(checks.Like);
        Assert.False(checks.Comment);
    }

    [Fact]
    public void SuggestChecks_OptionalCommentDoesNotDisableRequiredOtherActions()
    {
        var checks = InstagramTaskVerificationRules.SuggestChecks("Лайк\nКомментарий по желанию\nРепост\nСохранение");
        Assert.True(checks.Like);
        Assert.False(checks.Comment);
        Assert.True(checks.Repost);
        Assert.True(checks.Save);
    }

    [Fact]
    public void SuggestChecks_RequiredCommentIsNotAffectedByAnotherOptionalAction()
    {
        var checks = InstagramTaskVerificationRules.SuggestChecks("Комментарий, лайк по желанию");
        Assert.True(checks.Comment);
        Assert.True(checks.Like);
    }
}
