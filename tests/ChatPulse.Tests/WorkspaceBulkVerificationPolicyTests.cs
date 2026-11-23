using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceBulkVerificationPolicyTests
{
    [Theory]
    [InlineData("Лайк и репост", false, false, true)]
    [InlineData("Коментарий От души", true, false, false)]
    [InlineData("Комментарий по желанию, репост", false, false, true)]
    [InlineData("Сохранить, комментарий не нужен", false, true, false)]
    public void PostChecks_LikeAlwaysRequired_AndSuggestionsRespected(
        string text, bool comment, bool save, bool repost)
    {
        var checks = WorkspaceBulkTaskVerificationPolicy.PostChecks(text);
        Assert.True(checks.Like);
        Assert.Equal(comment, checks.Comment);
        Assert.Equal(save, checks.Save);
        Assert.Equal(repost, checks.Repost);
    }

    [Fact]
    public void SoftStop_PreventsNextTask()
    {
        Assert.True(WorkspaceBulkTaskVerificationPolicy.ShouldContinue(false));
        Assert.False(WorkspaceBulkTaskVerificationPolicy.ShouldContinue(true));
    }
}
