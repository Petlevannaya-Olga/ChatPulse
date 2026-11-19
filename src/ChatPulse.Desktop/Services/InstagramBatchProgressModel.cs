using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChatPulse.Desktop.Services;

public enum InstagramBatchStage
{
    SETTINGS,
    PARTICIPANTS,
    BROWSER,
    FOLLOWERS,
    FOLLOWING,
    FINISHING
}

public enum InstagramBatchStepState
{
    PENDING,
    ACTIVE,
    COMPLETED,
    SKIPPED,
    INTERRUPTED
}

public sealed class InstagramBatchStep(string title) : INotifyPropertyChanged
{
    private InstagramBatchStepState _state;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title { get; } = title;
    public InstagramBatchStepState State
    {
        get => _state;
        internal set
        {
            if (_state == value) return;
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Marker)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateColor)));
        }
    }
    public string Marker => State switch
    {
        InstagramBatchStepState.COMPLETED => "✓",
        InstagramBatchStepState.ACTIVE => "●",
        InstagramBatchStepState.INTERRUPTED => "!",
        _ => "○"
    };
    public string StateLabel => State switch
    {
        InstagramBatchStepState.COMPLETED => "Готово",
        InstagramBatchStepState.ACTIVE => "Выполняется",
        InstagramBatchStepState.SKIPPED => "Пропущено",
        InstagramBatchStepState.INTERRUPTED => "Прервано",
        _ => "Ожидает"
    };
    public string StateColor => State switch
    {
        InstagramBatchStepState.COMPLETED => "#27845A",
        InstagramBatchStepState.ACTIVE => "#8243C7",
        InstagramBatchStepState.INTERRUPTED => "#B04B47",
        _ => "#90839F"
    };
}

/// <summary>
/// A progress model for one batch. Only completed searches count toward progress;
/// a skipped direction is never misrepresented as a completed check.
/// </summary>
public sealed class InstagramBatchProgressModel : INotifyPropertyChanged
{
    private string _currentDetail = "Проверяем настройки Instagram...";
    private string _currentUser = string.Empty;
    private string _summary = string.Empty;
    private InstagramBatchResultSummary? _resultSummary;
    private int _totalSearches;
    private int _completedSearches;
    private int _found;
    private int _notFound;
    private int _inconclusive;
    private bool _stopRequested;
    private bool _isRunning = true;

    public InstagramBatchProgressModel()
    {
        Steps = [
            new InstagramBatchStep("Проверка настроек Instagram"),
            new InstagramBatchStep("Загрузка участниц"),
            new InstagramBatchStep("Запуск Chrome и проверка сессии"),
            new InstagramBatchStep("Проверка подписок на меня"),
            new InstagramBatchStep("Проверка моих подписок"),
            new InstagramBatchStep("Сохранение и завершение")
        ];
        Activate(InstagramBatchStage.SETTINGS);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<InstagramBatchStep> Steps { get; }
    public string CurrentDetail { get => _currentDetail; private set { _currentDetail = value; Notify(); } }
    public string CurrentUser { get => _currentUser; private set { _currentUser = value; Notify(); Notify(nameof(HasCurrentUser)); } }
    public bool HasCurrentUser => _currentUser.Length > 0;
    public string Summary { get => _summary; private set { _summary = value; Notify(); Notify(nameof(HasSummary)); } }
    public bool HasSummary => _summary.Length > 0 && _resultSummary is null;
    public InstagramBatchResultSummary? ResultSummary
    {
        get => _resultSummary;
        private set
        {
            _resultSummary = value;
            Notify();
            Notify(nameof(HasResultSummary));
            Notify(nameof(HasSummary));
        }
    }
    public bool HasResultSummary => _resultSummary is not null && !_isRunning;
    public int TotalSearches => _totalSearches;
    public int CompletedSearches => _completedSearches;
    public int Percentage => _totalSearches == 0 ? 0 : (int)(100L * _completedSearches / _totalSearches);
    public bool IsIndeterminate => _isRunning && _totalSearches == 0;
    public string ProgressLabel => _totalSearches == 0
        ? "Подготовка проверки..."
        : $"Проверено {_completedSearches} из {_totalSearches}";
    public string PercentageLabel => _totalSearches == 0 ? string.Empty : $"{Percentage}%";
    public string ResultsLabel => $"Найдено: {_found} · Не найдено: {_notFound} · С ошибкой: {_inconclusive}";
    public bool IsRunning => _isRunning;
    public bool CanStop => _isRunning && !_stopRequested;
    public bool CanAct => !_isRunning || !_stopRequested;
    public string ActionLabel => !_isRunning ? "Закрыть" : _stopRequested ? "Останавливаем..." : "Остановить";

    // Individual checks display a short status rather than batch statistics.
    public string IndividualProgressLabel => _stopRequested ? "Останавливаем проверку..." : "Проверяем Instagram...";

    public string IndividualResultLabel => _resultSummary switch
    {
        { Outcome: InstagramBatchOutcome.COMPLETED, Found: 1 } => "Подписка найдена",
        { Outcome: InstagramBatchOutcome.COMPLETED, NotFound: 1 } => "Подписка не найдена",
        { Outcome: InstagramBatchOutcome.COMPLETED } => "Проверка завершена",
        { Outcome: InstagramBatchOutcome.STOPPED } => "Проверка остановлена",
        _ => "Не удалось проверить подписку"
    };

    public string IndividualResultDetail => !string.IsNullOrWhiteSpace(_summary)
        ? _summary
        : _resultSummary?.Outcome == InstagramBatchOutcome.COMPLETED
            ? "Результат сохранён в таблице."
            : "Предыдущий результат сохранён без изменений.";

    public void Activate(InstagramBatchStage stage, string? detail = null)
    {
        if (!_isRunning) return;
        for (var i = 0; i < Steps.Count; i++)
        {
            if (i < (int)stage && Steps[i].State == InstagramBatchStepState.ACTIVE)
                Steps[i].State = InstagramBatchStepState.COMPLETED;
        }
        Steps[(int)stage].State = InstagramBatchStepState.ACTIVE;
        if (detail is not null) CurrentDetail = detail;
    }

    public void Skip(InstagramBatchStage stage)
    {
        if (!_isRunning) return;
        Steps[(int)stage].State = InstagramBatchStepState.SKIPPED;
    }

    public void ConfigureWorkload(int totalSearches)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSearches);
        _totalSearches = totalSearches;
        Notify(nameof(TotalSearches));
        NotifyProgress();
    }

    public void Report(InstagramBatchProgress progress, int found, int notFound, int inconclusive)
    {
        if (!_isRunning) return;
        _completedSearches = progress.Completed;
        _found = found;
        _notFound = notFound;
        _inconclusive = inconclusive;
        CurrentUser = $"Текущая участница: @{progress.UserName}";
        Notify(nameof(CompletedSearches));
        NotifyProgress();
        Notify(nameof(ResultsLabel));
    }

    public void RequestStop()
    {
        if (!_isRunning || _stopRequested) return;
        _stopRequested = true;
        CurrentDetail = "Останавливаем проверку. Сохраняем уже полученные результаты...";
        Notify(nameof(CanStop));
        Notify(nameof(CanAct));
        Notify(nameof(ActionLabel));
        Notify(nameof(IndividualProgressLabel));
    }

    public void InterruptActive()
    {
        if (!_isRunning) return;
        foreach (var step in Steps)
            if (step.State == InstagramBatchStepState.ACTIVE)
                step.State = InstagramBatchStepState.INTERRUPTED;
    }

    public void SetResultSummary(InstagramBatchResultSummary summary)
    {
        ResultSummary = summary;
    }

    public void Finish(string summary, bool successful)
    {
        if (!_isRunning) return;
        foreach (var step in Steps)
        {
            if (step.State == InstagramBatchStepState.ACTIVE)
                step.State = step == Steps[(int)InstagramBatchStage.FINISHING] || successful
                    ? InstagramBatchStepState.COMPLETED
                    : InstagramBatchStepState.INTERRUPTED;
        }
        _isRunning = false;
        CurrentUser = string.Empty;
        CurrentDetail = successful ? "Проверка завершена." : "Проверка остановлена или завершилась с ошибкой.";
        Summary = summary;
        Notify(nameof(IsRunning));
        Notify(nameof(HasResultSummary));
        Notify(nameof(HasSummary));
        Notify(nameof(IsIndeterminate));
        Notify(nameof(CanStop));
        Notify(nameof(CanAct));
        Notify(nameof(ActionLabel));
        Notify(nameof(IndividualResultLabel));
        Notify(nameof(IndividualResultDetail));
    }

    private void NotifyProgress()
    {
        Notify(nameof(Percentage));
        Notify(nameof(ProgressLabel));
        Notify(nameof(PercentageLabel));
        Notify(nameof(IsIndeterminate));
    }

    private void Notify([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
