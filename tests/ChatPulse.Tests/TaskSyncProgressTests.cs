using ChatPulse.Desktop.Views.Dialogs;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TaskSyncProgressTests
{
    [Fact]
    public void StageIsVisibleUntilChatFinishes()
    {
        var progress = new TaskSyncChatProgress("Чат А");
        Assert.Equal("Ожидает", progress.StatusLabel);
        progress.Start();
        Assert.Equal("Выполняется", progress.StatusLabel);
        progress.Report("Сохраняем историю в SQLite…");
        Assert.Equal("Сохраняем историю в SQLite…", progress.Detail);
        progress.Complete(4);
        Assert.Equal("Готово", progress.StatusLabel);
        Assert.Contains("4", progress.Detail);
    }

    [Fact]
    public void FailedChatRetainsFailureReason()
    {
        var progress = new TaskSyncChatProgress("Чат Б");
        progress.Start();
        progress.Fail("Нет подключения к Telegram");
        Assert.Equal("Ошибка", progress.StatusLabel);
        Assert.Equal("Нет подключения к Telegram", progress.Detail);
    }

    [Fact]
    public void StoppedChatIsNotMarkedSuccessful()
    {
        var progress = new TaskSyncChatProgress("Чат В");
        progress.Start();
        progress.Stop();
        Assert.Equal("Остановлено", progress.StatusLabel);
        Assert.DoesNotContain("Результат сохранён", progress.Detail);
    }
}
