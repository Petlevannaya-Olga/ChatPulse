using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ParticipantListSyncProgressModelTests
{
    [Fact]
    public void SearchIsIndeterminateAndReportsActualTelegramPages()
    {
        var model = new ParticipantListSyncProgressModel();
        Assert.Equal(ParticipantListSyncStepState.ACTIVE, model.Steps[0].State);
        Assert.True(model.CanStop);

        model.Report(new(ParticipantListSyncStage.TELEGRAM, "Проверяем доступ…"));
        model.Report(new(ParticipantListSyncStage.SEARCH, "Ищем…"));
        model.Report(new(ParticipantListSyncStage.SEARCH,
            "Просмотрено страниц: 3 · сообщений: 227", 3, 227));

        Assert.True(model.IsIndeterminate);
        Assert.Equal(3, model.PagesScanned);
        Assert.Equal(227, model.MessagesScanned);
        Assert.Contains("227", model.ProgressLabel);
        Assert.Equal(string.Empty, model.PercentageLabel);
        Assert.Equal(ParticipantListSyncStepState.COMPLETED, model.Steps[1].State);
        Assert.Equal(ParticipantListSyncStepState.ACTIVE, model.Steps[2].State);
    }

    [Fact]
    public void SaveDisablesCancellationUntilAtomicUpdateFinishes()
    {
        var model = new ParticipantListSyncProgressModel();
        model.Activate(ParticipantListSyncStage.SEARCH, "Поиск…");
        model.Activate(ParticipantListSyncStage.SAVE, "Сохранение…");

        Assert.False(model.CanStop);
        Assert.False(model.CanAct);
        model.RequestStop();
        Assert.Equal("Остановить", model.ActionLabel);
        model.Activate(ParticipantListSyncStage.DISPLAY, "Загрузка таблицы…");
        model.Finish("Список обновлён", "Готово", ParticipantListSyncOutcome.COMPLETED);

        Assert.Equal(100, model.Percentage);
        Assert.True(model.CanAct);
        Assert.Equal("Закрыть", model.ActionLabel);
    }

    [Fact]
    public void CancellationKeepsPartialScanCountButNoSuccessResult()
    {
        var model = new ParticipantListSyncProgressModel();
        model.Report(new(ParticipantListSyncStage.SEARCH, "Ищем…", 2, 190));
        model.RequestStop();

        Assert.False(model.CanStop);
        Assert.False(model.CanAct);
        Assert.Equal("Останавливаем…", model.ActionLabel);

        model.Finish("Синхронизация остановлена", "Список сохранён.",
            ParticipantListSyncOutcome.STOPPED);

        Assert.True(model.IsStopped);
        Assert.True(model.HasResult);
        Assert.Equal(ParticipantListSyncStepState.INTERRUPTED, model.Steps[2].State);
        Assert.Equal(190, model.MessagesScanned);
        Assert.Equal("Закрыть", model.ActionLabel);
    }

    [Fact]
    public void NoMatchingRegisterSkipsSaveButCompletesDisplay()
    {
        var model = new ParticipantListSyncProgressModel();
        model.Activate(ParticipantListSyncStage.SEARCH, "Ищем…");
        model.Skip(ParticipantListSyncStage.SAVE);
        model.Activate(ParticipantListSyncStage.DISPLAY, "Показываем сохранённый список…");
        model.Finish("Список не найден", "Список не изменён.",
            ParticipantListSyncOutcome.COMPLETED);

        Assert.Equal(ParticipantListSyncStepState.SKIPPED, model.Steps[3].State);
        Assert.Equal(ParticipantListSyncStepState.COMPLETED, model.Steps[4].State);
        Assert.Equal("Список не найден", model.ResultTitle);
    }
}
