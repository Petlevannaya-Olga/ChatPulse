using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// A short-lived manual execution session. Progress is intentionally not persisted:
/// a new session starts from the tasks still pending in SQLite.
/// The reader loads only the next task (not all cards into memory).
/// </summary>
public sealed class WorkspaceTaskExecutionSession(
    WorkspaceTaskReader reader,
    ChatSynchronizationService synchronization,
    ConfiguredChat chat,
    bool oldestFirst)
{
    private WorkspaceDetectedTask? _current;
    private bool _started;
    private int _initialTotal;
    private int _completed;

    public WorkspaceDetectedTask? Current => _current;
    public int InitialTotal => _initialTotal;
    public int Completed => _completed;
    public WorkspaceTaskExecutionProgress Progress => new(_initialTotal, _completed);

    public async Task<WorkspaceDetectedTask?> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("Сеанс выполнения уже запущен.");
        var page = await ReadNextAsync(cancellationToken);
        _initialTotal = page.Total;
        _current = page.Tasks.FirstOrDefault();
        _started = true;
        return _current;
    }

    public async Task<WorkspaceDetectedTask?> CompleteCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!_started || _current is null)
            throw new InvalidOperationException("Нет текущего невыполненного задания.");

        var messageId = _current.MessageId;
        // This is the same Telegram ❤️ and SQLite workflow as the existing 'Готово' button.
        await synchronization.CompleteTaskAsync(chat, messageId, cancellationToken);
        var nextPage = await ReadNextAsync(cancellationToken);
        if (nextPage.Tasks.Any(task => task.MessageId == messageId))
            throw new InvalidOperationException(
                "Telegram не подтвердил завершение задания. Оставляем его текущим.");

        _completed++;
        // Do not extend this session if newer tasks arrive while the dialog is open.
        _current = _completed >= _initialTotal ? null : nextPage.Tasks.FirstOrDefault();
        return _current;
    }

    private Task<WorkspaceTaskPage> ReadNextAsync(CancellationToken cancellationToken)
        => reader.ReadPageAsync(chat, WorkspaceTaskState.Pending, oldestFirst,
            offset: 0, pageSize: 1, cancellationToken: cancellationToken);
}

public readonly record struct WorkspaceTaskExecutionProgress(int Total, int Completed)
{
    public int Remaining => Math.Max(0, Total - Completed);
    public int CurrentNumber => Total == 0 || Remaining == 0 ? 0 : Completed + 1;
    public double Fraction => Total == 0 ? 1 : Math.Clamp((double)Completed / Total, 0, 1);
}
