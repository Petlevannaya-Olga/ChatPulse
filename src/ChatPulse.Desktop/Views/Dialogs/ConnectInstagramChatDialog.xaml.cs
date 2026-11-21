using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Services;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class ConnectInstagramChatDialog : Window, INotifyPropertyChanged
{
    private readonly InstagramChatConnectionService _connection;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private readonly List<TelegramChatOption> _allChats = [];
    private long _analysisGeneration;
    private TelegramChatOption? _verifiedChat;
    private bool _busy;
    private bool _completed;
    private string _status = "Загружаем список Telegram-чатов…";

    public ConnectInstagramChatDialog(InstagramChatConnectionService connection)
    {
        _connection = connection;
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) => await RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<TelegramChatOption> VisibleChats { get; } = [];
    public string Status
    {
        get => _status;
        private set { _status = value; Notify(); }
    }
    public bool CanRefresh => !_busy;
    public bool CanConnect => !_busy && _verifiedChat is not null &&
        Equals(ChatList.SelectedItem, _verifiedChat);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ChatList.IsEnabled = !busy;
        ChatSearch.IsEnabled = !busy;
        Notify(nameof(CanRefresh));
        Notify(nameof(CanConnect));
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        ++_analysisGeneration;
        _verifiedChat = null;
        SetBusy(true);
        Status = "Получаем доступные чаты Telegram…";
        try
        {
            var chats = await _connection.GetAvailableChatsAsync(_cancellation.Token);
            _allChats.Clear();
            _allChats.AddRange(chats);
            FilterChats();
            Status = chats.Count == 0
                ? "Нет доступных постчатов для подключения. Проверьте Telegram и названия чатов."
                : $"Найдено чатов: {chats.Count}. Выберите чат для автоматической проверки.";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { /*ignore*/}
        catch (Exception exception)
        {
            Status = $"Не удалось получить чаты: {exception.Message}";
        }
        finally { SetBusy(false); }
    }

    private void FilterChats()
    {
        var query = ChatSearch.Text.Trim();
        VisibleChats.Clear();
        foreach (var chat in _allChats.Where(chat =>
                     chat.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            VisibleChats.Add(chat);
    }

    private void ChatSearch_OnTextChanged(object sender, TextChangedEventArgs args) => FilterChats();

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs args) => await RefreshAsync();

    private async void ChatList_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        var selected = ChatList.SelectedItem as TelegramChatOption;
        var generation = ++_analysisGeneration;
        _verifiedChat = null;
        Notify(nameof(CanConnect));
        if (selected is null || _busy) return;
        Status = $"Проверяем «{selected.Title}»…";
        try
        {
            await _analysisGate.WaitAsync(_cancellation.Token);
            try
            {
                if (generation != _analysisGeneration) return;
                var result = await _connection.AnalyzeAsync(selected, _cancellation.Token);
                if (generation != _analysisGeneration || _cancellation.IsCancellationRequested) return;
                _verifiedChat = result.IsInstagram ? selected : null;
                Status = result.Description;
                Notify(nameof(CanConnect));
            }
            finally { _analysisGate.Release(); }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { /*ignore*/}
        catch (Exception exception)
        {
            if (generation == _analysisGeneration)
                Status = $"Ошибка проверки чата: {exception.Message}";
        }
    }

    private async void ConnectButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (!CanConnect || _verifiedChat is null) return;
        var chat = _verifiedChat;
        ++_analysisGeneration;
        SetBusy(true);
        Status = "Подключаем чат и загружаем задания за 7 дней…";
        try
        {
            var count = await _connection.ConnectAsync(chat, _cancellation.Token);
            Status = $"Чат подключён. Загружено сообщений: {count}.";
            _completed = true;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            Status = "Подключение остановлено.";
        }
        catch (Exception exception)
        {
            Status = $"Не удалось подключить чат: {exception.Message}. Можно повторить попытку.";
        }
        finally { if (!_completed) SetBusy(false); }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs args)
    {
        ++_analysisGeneration;
        _cancellation.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        ++_analysisGeneration;
        _cancellation.Cancel();
        base.OnClosed(e);
    }

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
