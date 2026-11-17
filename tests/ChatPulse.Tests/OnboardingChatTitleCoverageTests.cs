using ChatPulse.Contracts.Telegram;
using Xunit;

namespace ChatPulse.Tests;

/// <summary>The hard chat-title rule must not be loosened by onboarding UI changes.</summary>
public sealed class OnboardingChatTitleCoverageTests
{
    [Theory]
    [InlineData("пост")]
    [InlineData("ПОСТ")]
    [InlineData("Пост чат")]
    [InlineData("14 Пост чат б/с")]
    [InlineData("Пост-чат")]
    [InlineData("чат/пост")]
    [InlineData("[пост]")]
    [InlineData("#пост")]
    [InlineData("пост!")]
    [InlineData("чат\nПОСТ\tактивности")]
    [InlineData("пост🙂чат")]
    public void StandalonePostWord_IsEligible(string title)
        => Assert.True(ChatTitlePolicy.IsEligible(title));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("репост")]
    [InlineData("Репосты")]
    [InlineData("посты")]
    [InlineData("постинг")]
    [InlineData("антипост")]
    [InlineData("пост123")]
    [InlineData("12пост")]
    [InlineData("пост_чат")]
    [InlineData("чат_пост")]
    [InlineData("постчат")]
    [InlineData("посты чат")]
    [InlineData("пост́ чат")]
    [InlineData("Instagram чат")]
    public void NonStandaloneOrMissingWord_IsRejected(string title)
        => Assert.False(ChatTitlePolicy.IsEligible(title));

    [Fact]
    public void NullChatTitle_IsRejected()
        => Assert.False(ChatTitlePolicy.IsEligible(null));
}
