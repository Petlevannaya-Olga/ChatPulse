using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ChatPulse.Desktop.Views.Dialogs;

public sealed class TaskSyncChatProgress(string title) : INotifyPropertyChanged
{
    private string _statusLabel = "Ожидает";
    private string _detail = "Ожидает своей очереди.";
    private Brush _statusBrush = Brushes.Gray;

    public string Title { get; } = title;
    public string StatusLabel
    {
        get => _statusLabel;
        private set { _statusLabel = value; Notify(); }
    }
    public string Detail
    {
        get => _detail;
        private set { _detail = value; Notify(); }
    }
    public Brush StatusBrush
    {
        get => _statusBrush;
        private set { _statusBrush = value; Notify(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start()
    {
        StatusLabel = "Выполняется";
        StatusBrush = Brushes.MediumPurple;
        Detail = "Проверяем доступ к Telegram…";
    }

    public void Report(string stage) => Detail = stage;

    public void Complete(int count)
    {
        StatusLabel = "Готово";
        StatusBrush = Brushes.ForestGreen;
        Detail = $"Новых сообщений: {count}. Результат сохранён.";
    }

    public void Fail(string reason)
    {
        StatusLabel = "Ошибка";
        StatusBrush = Brushes.Firebrick;
        Detail = reason;
    }

    public void Stop()
    {
        StatusLabel = "Остановлено";
        StatusBrush = Brushes.DarkOrange;
        Detail = "Синхронизация этого чата не завершена.";
    }

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class TaskSyncProgressDialog : Window, INotifyPropertyChanged
{
    private readonly CancellationTokenSource _cancellation;
    private bool _started;
    private bool _running = true;
    private int _finished;
    private int _failed;
    private string _currentChatLabel = "Подготовка…";
    private string _currentStage = "Ожидаем начала синхронизации.";
    private string _finalMessage = "Можно остановить после текущего шага.";

    public TaskSyncProgressDialog(string scopeTitle, IEnumerable<string> chatTitles,
        CancellationTokenSource cancellation)
    {
        ScopeTitle = scopeTitle;
        _cancellation = cancellation;
        Chats = new ObservableCollection<TaskSyncChatProgress>(chatTitles.Select(title => new TaskSyncChatProgress(title)));
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) =>
        {
            if (_started) return;
            _started = true;
            StartRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? StartRequested;
    public string ScopeTitle { get; }
    public ObservableCollection<TaskSyncChatProgress> Chats { get; }
    public string CurrentChatLabel
    {
        get => _currentChatLabel;
        private set { _currentChatLabel = value; Notify(); }
    }
    public string CurrentStage
    {
        get => _currentStage;
        private set { _currentStage = value; Notify(); }
    }
    public string FinalMessage
    {
        get => _finalMessage;
        private set { _finalMessage = value; Notify(); }
    }
    public string SummaryLabel => $"Обработано чатов: {_finished} из {Chats.Count}. Ошибок: {_failed}.";
    public double Percentage => Chats.Count == 0 ? 0 : 100.0 * _finished / Chats.Count;
    public string ActionLabel => _running ? "Остановить" : "Закрыть";

    public void BeginChat(int index)
    {
        CurrentChatLabel = $"Чат {index + 1} из {Chats.Count}: {Chats[index].Title}";
        CurrentStage = "Проверяем доступ к Telegram…";
        Chats[index].Start();
    }

    public void ReportStage(int index, string stage)
    {
        CurrentStage = stage;
        Chats[index].Report(stage);
    }

    public void CompleteChat(int index, int added)
    {
        Chats[index].Complete(added);
        Advance();
    }

    public void FailChat(int index, string reason)
    {
        Chats[index].Fail(reason);
        _failed++;
        Advance();
    }

    public void StopChat(int index)
    {
        Chats[index].Stop();
        Advance();
    }

    private void Advance()
    {
        _finished++;
        Notify(nameof(SummaryLabel));
        Notify(nameof(Percentage));
    }

    public void Finish(string message)
    {
        CurrentChatLabel = "Синхронизация завершена";
        CurrentStage = message;
        FinalMessage = message;
        _running = false;
        Notify(nameof(ActionLabel));
    }

    private void ActionButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (!_running) { Close(); return; }
        RequestStop();
    }

    private void RequestStop()
    {
        if (_cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        CurrentStage = "Останавливаем синхронизацию…";
        FinalMessage = "Ожидаем завершения текущей операции Telegram.";
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (_running)
        {
            RequestStop();
            args.Cancel = true;
        }
        base.OnClosing(args);
    }

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
