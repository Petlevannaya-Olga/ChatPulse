using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ChatPulse.Desktop.Views.Dialogs;

/// <summary>
/// Progress for the explicit full-history action. All updates must be made on the
/// window dispatcher; the Telegram reader may invoke callbacks on worker threads.
/// </summary>
public partial class ParticipantHistoryProgressDialog : Window, INotifyPropertyChanged
{
    private readonly CancellationTokenSource _cancellation;
    private bool _started;
    private bool _isRunning = true;
    private bool _isActionEnabled = true;
    private string _statusText = "Подключаемся к Telegram…";
    private string _progressText = "История будет загружена за весь доступный период.";
    private string _resultDetails = string.Empty;
    private string _actionLabel = "Остановить";

    public ParticipantHistoryProgressDialog(string chatTitle, CancellationTokenSource cancellation)
    {
        ChatTitle = chatTitle;
        _cancellation = cancellation;
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
    public string ChatTitle { get; }
    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; Notify(); }
    }
    public bool IsActionEnabled
    {
        get => _isActionEnabled;
        private set { _isActionEnabled = value; Notify(); }
    }
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; Notify(); }
    }
    public string ProgressText
    {
        get => _progressText;
        private set { _progressText = value; Notify(); }
    }
    public string ResultDetails
    {
        get => _resultDetails;
        private set { _resultDetails = value; Notify(); }
    }
    public string ActionLabel
    {
        get => _actionLabel;
        private set { _actionLabel = value; Notify(); }
    }

    public void SetPageProgress(int pages, int scanned, int found)
    {
        if (!IsRunning || _cancellation.IsCancellationRequested) return;
        StatusText = "Читаем доступную историю Telegram…";
        ProgressText = $"Страниц: {pages}. Сообщений просмотрено: {scanned}. Списков найдено: {found}.";
    }

    public void SetParsingProgress(int processed, int total)
    {
        if (!IsRunning || _cancellation.IsCancellationRequested) return;
        StatusText = "Разбираем списки участниц по порядку публикации…";
        ProgressText = $"Обработано списков: {processed} из {total}.";
    }

    public void SetSaving()
    {
        if (!IsRunning || _cancellation.IsCancellationRequested) return;
        StatusText = "Проверяем и сохраняем исторический реестр в SQLite…";
    }

    public void Finish(string title, string details)
    {
        StatusText = title;
        ResultDetails = details;
        ProgressText = string.Empty;
        IsRunning = false;
        ActionLabel = "Закрыть";
        IsActionEnabled = true;
    }

    private void ActionButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (!IsRunning)
        {
            Close();
            return;
        }
        RequestStop();
    }

    private void RequestStop()
    {
        if (_cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        StatusText = "Останавливаем загрузку…";
        IsActionEnabled = false;
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (IsRunning)
        {
            RequestStop();
            args.Cancel = true;
        }
        base.OnClosing(args);
    }

    private void Notify([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
