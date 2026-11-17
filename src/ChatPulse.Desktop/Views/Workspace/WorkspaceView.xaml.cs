using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Views.Workspace;

public partial class WorkspaceView : UserControl, INotifyPropertyChanged
{
    private ISettingsService? _settings;
    private WorkspaceTaskReader? _reader;
    private WorkspaceChatRow? _selectedChat;
    private string _taskSummary = string.Empty;
    private bool _isBusy;
    private int _selectionVersion;
    private bool _isRebuildingChatList;

    public WorkspaceView()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<WorkspaceChatRow> Chats { get; } = [];
    public ObservableCollection<WorkspaceDetectedTask> Tasks { get; } = [];
    public WorkspaceChatRow? SelectedChat
    {
        get => _selectedChat;
        set { _selectedChat = value; Notify(); Notify(nameof(SelectedChatTitle)); }
    }
    public string SelectedChatTitle => SelectedChat?.Title ?? "Выберите чат";
    public string TaskSummary { get => _taskSummary; private set { _taskSummary = value; Notify(); } }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Notify(); Notify(nameof(HasTasks)); Notify(nameof(HasNoTasks)); } }
    public bool HasTasks => !IsBusy && Tasks.Count > 0;
    public bool HasNoTasks => !IsBusy && Tasks.Count == 0;

    public void Initialize(ISettingsService settings, WorkspaceTaskReader reader)
    {
        _settings = settings;
        _reader = reader;
    }

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (IsVisible && _settings is not null && _reader is not null)
            await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var settings = await _settings!.LoadAsync();
            _isRebuildingChatList = true;
            try
            {
                Chats.Clear();
                foreach (var chat in settings.Chats.Where(chat =>
                             chat.SynchronizationState == ChatSynchronizationState.COMPLETED))
                    Chats.Add(new WorkspaceChatRow(chat));
                SelectedChat = Chats.FirstOrDefault();
            }
            finally
            {
                _isRebuildingChatList = false;
            }
            if (SelectedChat is not null)
                await LoadTasksAsync(SelectedChat);
            else
            {
                Tasks.Clear();
                TaskSummary = "Подключённых чатов пока нет.";
                Notify(nameof(HasTasks));
                Notify(nameof(HasNoTasks));
            }
        }
        catch (Exception)
        {
            TaskSummary = "Не удалось загрузить список чатов.";
        }
    }

    private async void ChatList_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_isRebuildingChatList && ChatList.SelectedItem is WorkspaceChatRow row && _reader is not null)
            await LoadTasksAsync(row);
    }

    private async Task LoadTasksAsync(WorkspaceChatRow row)
    {
        var version = ++_selectionVersion;
        IsBusy = true;
        Tasks.Clear();
        try
        {
            var tasks = await _reader!.GetTasksAsync(row.Chat);
            if (version != _selectionVersion) return;
            foreach (var task in tasks) Tasks.Add(task);
            TaskSummary = Tasks.Count == 0
                ? "В загруженной истории не обнаружено заданий по текущим правилам анализа."
                : $"Предварительно найдено заданий: {Tasks.Count}. Откройте публикацию, чтобы посмотреть задание.";
        }
        catch (Exception)
        {
            if (version == _selectionVersion)
                TaskSummary = "Не удалось прочитать задания из локальной базы данных.";
        }
        finally
        {
            if (version == _selectionVersion)
            {
                IsBusy = false;
                Notify(nameof(HasTasks));
                Notify(nameof(HasNoTasks));
            }
        }
    }

    private void OpenPostButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string link } ||
            !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("t.me" or "instagram.com" or "www.instagram.com")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show("Не удалось открыть публикацию.", "ChatPulse", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed record WorkspaceChatRow(ConfiguredChat Chat)
{
    public string Title => Chat.DisplayName.Length > 0 ? Chat.DisplayName : Chat.TelegramTitle;
    public string Network => Chat.SocialNetwork;
}
