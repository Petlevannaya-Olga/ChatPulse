using System.IO;
using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramBatchProgressModelTests
{
    [Fact]
    public async Task ClosingManagedBrowserDoesNotDeletePersistedProfile()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"chatpulse-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var marker = Path.Combine(profile, "session-marker.txt");
        File.WriteAllText(marker, "retained");
        try
        {
            using var service = new InstagramBrowserService(profile);
            await service.CloseBrowserAsync();
            await service.CloseBrowserAsync();
            Assert.Equal("retained", File.ReadAllText(marker));
        }
        finally
        {
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public void StartsWithSettingsBeforeAnyBrowserWork()
    {
        var model = new InstagramBatchProgressModel();

        Assert.Equal(InstagramBatchStepState.ACTIVE, model.Steps[(int)InstagramBatchStage.SETTINGS].State);
        Assert.All(model.Steps.Skip(1), step =>
            Assert.Equal(InstagramBatchStepState.PENDING, step.State));
        Assert.True(model.IsIndeterminate);
        Assert.Equal(0, model.Percentage);
        Assert.Equal("Подготовка проверки...", model.ProgressLabel);
        Assert.Equal(string.Empty, model.PercentageLabel);
        Assert.True(model.CanStop);
    }

    [Fact]
    public void CountsOnlyCompletedSearchesAndTracksRealProgress()
    {
        var model = new InstagramBatchProgressModel();
        model.Activate(InstagramBatchStage.PARTICIPANTS);
        model.ConfigureWorkload(7);
        model.Activate(InstagramBatchStage.BROWSER);
        model.Activate(InstagramBatchStage.FOLLOWERS);
        model.Report(new InstagramBatchProgress("first.user", "followers",
            new InstagramSearchResult(InstagramSearchStatus.FOUND, "ok"), 1, 7), 1, 0, 0);
        model.Report(new InstagramBatchProgress("second.user", "followers",
            new InstagramSearchResult(InstagramSearchStatus.INCONCLUSIVE, "error"), 2, 7), 1, 0, 1);

        Assert.Equal(2, model.CompletedSearches);
        Assert.Equal(28, model.Percentage);
        Assert.Equal("Проверено 2 из 7", model.ProgressLabel);
        Assert.Equal("28%", model.PercentageLabel);
        Assert.False(model.IsIndeterminate);
        Assert.Equal("Текущая участница: @second.user", model.CurrentUser);
        Assert.Contains("С ошибкой: 1", model.ResultsLabel);
        Assert.Equal(InstagramBatchStepState.COMPLETED,
            model.Steps[(int)InstagramBatchStage.BROWSER].State);
        Assert.Equal(InstagramBatchStepState.ACTIVE,
            model.Steps[(int)InstagramBatchStage.FOLLOWERS].State);
    }

    [Fact]
    public void SkipFollowersKeepsVipOnlyCheckDistinct()
    {
        var model = new InstagramBatchProgressModel();
        model.ConfigureWorkload(2);
        model.Skip(InstagramBatchStage.FOLLOWERS);
        model.Activate(InstagramBatchStage.BROWSER);
        model.Activate(InstagramBatchStage.FOLLOWING);

        Assert.Equal(InstagramBatchStepState.SKIPPED,
            model.Steps[(int)InstagramBatchStage.FOLLOWERS].State);
        Assert.Equal(InstagramBatchStepState.ACTIVE,
            model.Steps[(int)InstagramBatchStage.FOLLOWING].State);
    }

    [Fact]
    public void StopDisablesFurtherStopRequestsButEnablesClosingAfterFinish()
    {
        var model = new InstagramBatchProgressModel();
        model.ConfigureWorkload(10);
        model.Activate(InstagramBatchStage.FOLLOWERS);
        model.Report(new InstagramBatchProgress("member", "followers",
            new InstagramSearchResult(InstagramSearchStatus.FOUND, "ok"), 3, 10), 3, 0, 0);
        model.RequestStop();

        Assert.False(model.CanStop);
        Assert.False(model.CanAct);
        Assert.Equal("Останавливаем...", model.ActionLabel);
        Assert.Equal(30, model.Percentage);

        model.InterruptActive();
        model.Activate(InstagramBatchStage.FINISHING);
        model.Finish("Остановлено; три результата сохранены.", successful: false);

        Assert.False(model.IsRunning);
        Assert.True(model.CanAct);
        Assert.Equal("Закрыть", model.ActionLabel);
        Assert.Equal(InstagramBatchStepState.INTERRUPTED,
            model.Steps[(int)InstagramBatchStage.FOLLOWERS].State);
        Assert.Equal(InstagramBatchStepState.COMPLETED,
            model.Steps[(int)InstagramBatchStage.FINISHING].State);
        Assert.Equal(3, model.CompletedSearches);
    }
    [Fact]
    public void CompletedRunUsesStructuredCardInsteadOfTextSummary()
    {
        var model = new InstagramBatchProgressModel();
        model.ConfigureWorkload(81);
        var result = new InstagramBatchResultSummary(
            DateTimeOffset.UtcNow, InstagramBatchOutcome.COMPLETED,
            81, 81, 71, 10, 0, 17, 0);
        model.SetResultSummary(result);
        Assert.False(model.HasResultSummary); // The operation is still running.

        model.Activate(InstagramBatchStage.FINISHING);
        model.Finish("Проверка завершена: 81 из 81 поисков...", successful: true);

        Assert.True(model.HasResultSummary);
        Assert.False(model.HasSummary);
        Assert.Same(result, model.ResultSummary);
        Assert.Equal("Закрыть", model.ActionLabel);
    }

    [Fact]
    public void FailureWithoutStructuredResultStillShowsErrorText()
    {
        var model = new InstagramBatchProgressModel();
        model.Finish("Не удалось прочитать настройки Instagram.", successful: false);

        Assert.False(model.HasResultSummary);
        Assert.True(model.HasSummary);
    }

    [Theory]
    [InlineData(1, 0, "Подписка найдена")]
    [InlineData(0, 1, "Подписка не найдена")]
    public void IndividualCheckShowsShortOutcome(int found, int notFound, string expected)
    {
        var model = new InstagramBatchProgressModel();
        model.ConfigureWorkload(1);
        model.SetResultSummary(new InstagramBatchResultSummary(
            DateTimeOffset.UtcNow, InstagramBatchOutcome.COMPLETED,
            1, 1, found, notFound, 0, 0, 0));
        model.Finish(string.Empty, successful: true);

        Assert.Equal(expected, model.IndividualResultLabel);
        Assert.Equal("Результат сохранён в таблице.", model.IndividualResultDetail);
        Assert.Equal("Закрыть", model.ActionLabel);
    }

    [Fact]
    public void FailedIndividualCheckKeepsErrorWithoutBatchStatistics()
    {
        var model = new InstagramBatchProgressModel();
        model.Finish("Instagram недоступен. Предыдущий результат сохранён.", successful: false);

        Assert.Equal("Не удалось проверить подписку", model.IndividualResultLabel);
        Assert.Contains("Предыдущий результат сохранён", model.IndividualResultDetail);
    }

    [Fact]
    public void IndividualProgressTextUpdatesOnStop()
    {
        var model = new InstagramBatchProgressModel();
        Assert.Equal("Проверяем Instagram...", model.IndividualProgressLabel);
        model.RequestStop();
        Assert.Equal("Останавливаем проверку...", model.IndividualProgressLabel);
    }

    [Fact]
    public void StoppedIndividualCheckShowsStoppedStatus()
    {
        var model = new InstagramBatchProgressModel();
        model.SetResultSummary(new InstagramBatchResultSummary(
            DateTimeOffset.UtcNow, InstagramBatchOutcome.STOPPED,
            0, 1, 0, 0, 0, 0, 0));
        model.Finish("Проверка остановлена. Статус не изменён.", successful: false);

        Assert.Equal("Проверка остановлена", model.IndividualResultLabel);
        Assert.Contains("Статус не изменён", model.IndividualResultDetail);
    }

}
