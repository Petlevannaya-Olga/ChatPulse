using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChatPulse.Desktop.Services;

public enum ParticipantListSyncOutcome
{
    COMPLETED,
    STOPPED,
    FAILED
}

public enum ParticipantListSyncStepState
{
    PENDING,
    ACTIVE,
    COMPLETED,
    SKIPPED,
    INTERRUPTED
}

public sealed class ParticipantListSyncStep(string title) : INotifyPropertyChanged
{
    private ParticipantListSyncStepState _state;

    public string Title { get; } = title;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ParticipantListSyncStepState State
    {
        get => _state;
        internal set
        {
            if (_state == value) return;
            _state = value;
            PropertyChanged?.Invoke(this, new(nameof(State)));
            PropertyChanged?.Invoke(this, new(nameof(Marker)));
            PropertyChanged?.Invoke(this, new(nameof(StateLabel)));
            PropertyChanged?.Invoke(this, new(nameof(StateColor)));
        }
    }

    public string Marker => State switch
    {
        ParticipantListSyncStepState.COMPLETED => "✓",
        ParticipantListSyncStepState.ACTIVE => "●",
        ParticipantListSyncStepState.INTERRUPTED => "!",
        ParticipantListSyncStepState.SKIPPED => "–",
        _ => "○"
    };

    public string StateLabel => State switch
    {
        ParticipantListSyncStepState.COMPLETED => "Готово",
        ParticipantListSyncStepState.ACTIVE => "Выполняется",
        ParticipantListSyncStepState.INTERRUPTED => "Прервано",
        ParticipantListSyncStepState.SKIPPED => "Пропущено",
        _ => "Ожидает"
    };

    public string StateColor => State switch
    {
        ParticipantListSyncStepState.COMPLETED => "#278857",
        ParticipantListSyncStepState.ACTIVE => "#8243C7",
        ParticipantListSyncStepState.INTERRUPTED => "#BA4545",
        _ => "#9587A7"
    };
}

public sealed class ParticipantListSyncProgressModel : INotifyPropertyChanged
{
    private ParticipantListSyncStage _activeStage = ParticipantListSyncStage.SETTINGS;
    private string _detail = "Проверяем настройки выбранного чата…";
    private bool _isRunning = true;
    private bool _stopRequested;
    private string _resultTitle = string.Empty;
    private string _resultDetail = string.Empty;
    private ParticipantListSyncOutcome _outcome;
    private int _pagesScanned;
    private int _messagesScanned;
    private string _sourceMessageDateLabel = string.Empty;

    public ParticipantListSyncProgressModel()
    {
        Steps =
        [
            new("Проверка настроек чата") { State = ParticipantListSyncStepState.ACTIVE },
            new("Telegram и доступ к чату"),
            new("Поиск последнего списка участниц"),
            new("Обновление списка в SQLite"),
            new("Обновление таблицы участниц")
        ];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ParticipantListSyncStep> Steps { get; }

    public bool IsRunning => _isRunning;
    public bool HasResult => !_isRunning;
    public string ResultTitle => _resultTitle;
    public string ResultDetail => _resultDetail;
    public bool IsStopped => HasResult && _outcome == ParticipantListSyncOutcome.STOPPED;
    public string CurrentDetail => _detail;
    public int PagesScanned => _pagesScanned;
    public int MessagesScanned => _messagesScanned;
    public string SourceMessageDateLabel => _sourceMessageDateLabel;
    public bool HasSourceMessageDate => IsRunning && _sourceMessageDateLabel.Length > 0;
    public bool IsIndeterminate => IsRunning && _activeStage == ParticipantListSyncStage.SEARCH;
    public bool CanStop => IsRunning && !_stopRequested && _activeStage < ParticipantListSyncStage.SAVE;
    public bool CanAct => !IsRunning || CanStop;
    public string ActionLabel => !IsRunning ? "Закрыть" : _stopRequested ? "Останавливаем…" : "Остановить";
    public string ProgressLabel => IsIndeterminate
        ? $"Просмотрено страниц: {_pagesScanned} · сообщений: {_messagesScanned}"
        : $"Этап {(int)_activeStage + 1} из {Steps.Count}";
    public int Percentage => !IsRunning && _outcome == ParticipantListSyncOutcome.COMPLETED
        ? 100 : (int)_activeStage * 100 / Steps.Count;
    public string PercentageLabel => IsIndeterminate ? string.Empty : $"{Percentage}%";

    public void Report(ParticipantListSyncProgress progress)
    {
        if (!IsRunning) return;
        Activate(progress.Stage, progress.Description);
        if (progress.SourceMessageSentAtUtc is { } timestamp)
            SetSourceMessageDate(timestamp);
        if (progress.Stage != ParticipantListSyncStage.SEARCH) return;
        _pagesScanned = Math.Max(_pagesScanned, progress.PagesScanned);
        _messagesScanned = Math.Max(_messagesScanned, progress.MessagesScanned);
        Notify(nameof(PagesScanned));
        Notify(nameof(MessagesScanned));
        Notify(nameof(ProgressLabel));
    }

    public void Activate(ParticipantListSyncStage stage, string detail)
    {
        if (!IsRunning || stage < _activeStage) return;
        for (var i = (int)_activeStage; i < (int)stage; i++)
            if (Steps[i].State == ParticipantListSyncStepState.ACTIVE)
                Steps[i].State = ParticipantListSyncStepState.COMPLETED;
        _activeStage = stage;
        if (Steps[(int)stage].State == ParticipantListSyncStepState.PENDING)
            Steps[(int)stage].State = ParticipantListSyncStepState.ACTIVE;
        _detail = detail;
        UpdateProgressProperties();
    }

    public void SetSourceMessageDate(DateTime dateUtc)
    {
        _sourceMessageDateLabel = $"Сообщение со списком от {new ParticipantListSourceInfo(0, dateUtc).DateLabel}";
        Notify(nameof(SourceMessageDateLabel));
        Notify(nameof(HasSourceMessageDate));
    }

    public void Skip(ParticipantListSyncStage stage)
    {
        if (!IsRunning) return;
        Steps[(int)stage].State = ParticipantListSyncStepState.SKIPPED;
    }

    public void RequestStop()
    {
        if (!CanStop) return;
        _stopRequested = true;
        Notify(nameof(CanStop));
        Notify(nameof(CanAct));
        Notify(nameof(ActionLabel));
    }

    public void Finish(string title, string detail, ParticipantListSyncOutcome outcome)
    {
        if (!IsRunning) return;
        var current = Steps[(int)_activeStage];
        if (current.State == ParticipantListSyncStepState.ACTIVE)
            current.State = outcome == ParticipantListSyncOutcome.COMPLETED
                ? ParticipantListSyncStepState.COMPLETED
                : ParticipantListSyncStepState.INTERRUPTED;
        _outcome = outcome;
        _resultTitle = title;
        _resultDetail = detail;
        _isRunning = false;
        Notify(nameof(IsRunning));
        Notify(nameof(HasSourceMessageDate));
        Notify(nameof(HasResult));
        Notify(nameof(IsStopped));
        Notify(nameof(ResultTitle));
        Notify(nameof(ResultDetail));
        UpdateProgressProperties();
    }

    private void UpdateProgressProperties()
    {
        Notify(nameof(CurrentDetail));
        Notify(nameof(IsIndeterminate));
        Notify(nameof(ProgressLabel));
        Notify(nameof(Percentage));
        Notify(nameof(PercentageLabel));
        Notify(nameof(CanStop));
        Notify(nameof(CanAct));
        Notify(nameof(ActionLabel));
    }

    private void Notify([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new(propertyName));
}
