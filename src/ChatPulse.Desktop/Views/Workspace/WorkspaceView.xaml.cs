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
    private enum WorkspacePage { Home, Chat, Settings }
    private enum ChatPage { Tasks, Subscriptions, Members }

    private ISettingsService? _settings;
    private WorkspaceTaskReader? _reader;
    private WorkspaceChatRow? _selectedChat;
    private readonly Dictionary<WorkspaceChatRow, IReadOnlyList<WorkspaceDetectedTask>> _taskCache = [];
    private WorkspacePage _page = WorkspacePage.Home;
    private ChatPage _chatPage = ChatPage.Tasks;
    private string _taskSummary = string.Empty;
    private string _loadMessage = string.Empty;
    private bool _isBusy;
    private bool _isSidebarExpanded = true;
    private bool _showCompleted;
    private bool _isRebuildingChatList;
    private int _loadVersion;
    private int _totalDetected;

    public WorkspaceView()
    {
        InitializeComponent();
        DataContext = this;
        UpdateNavigationVisuals();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<WorkspaceChatRow> Chats { get; } = [];
    public ObservableCollection<WorkspaceTaskCard> Tasks { get; } = [];

    public WorkspaceChatRow? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (ReferenceEquals(_selectedChat, value)) return;
            _selectedChat = value;
            Notify();
            Notify(nameof(SelectedChatTitle));
            Notify(nameof(SelectedChatNetwork));
            Notify(nameof(SelectedChatTaskCount));
        }
    }

    public string SelectedChatTitle => SelectedChat?.Title ?? "Выберите чат";
    public string SelectedChatNetwork => SelectedChat?.Network ?? string.Empty;
    public string SelectedChatTaskCount => SelectedChat is null ? "—" : SelectedChat.DetectedCount.ToString();
    public string TotalDetected => _totalDetected.ToString();
    public string ConnectedChatCount => Chats.Count.ToString();
    public string TaskSummary { get => _taskSummary; private set { _taskSummary = value; Notify(); } }
    public string LoadMessage { get => _loadMessage; private set { _loadMessage = value; Notify(); } }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Notify(); Notify(nameof(HasTasks)); Notify(nameof(HasNoTasks)); } }
    public bool HasTasks => !IsBusy && Tasks.Count > 0 && !_showCompleted;
    public bool HasNoTasks => !IsBusy && (Tasks.Count == 0 || _showCompleted);
    public string EmptyTasksMessage => _showCompleted
        ? "Учёт выполненных заданий будет доступен после добавления отметки «Готово»."
        : "В загруженной истории этого чата пока нет распознанных заданий.";
    public bool IsHomeVisible => _page == WorkspacePage.Home;
    public bool IsChatVisible => _page == WorkspacePage.Chat;
    public bool IsSettingsVisible => _page == WorkspacePage.Settings;
    public bool IsTasksVisible => _chatPage == ChatPage.Tasks;
    public bool IsSubscriptionsVisible => _chatPage == ChatPage.Subscriptions;
    public bool IsMembersVisible => _chatPage == ChatPage.Members;
    public bool IsSidebarExpanded => _isSidebarExpanded;

    public void Initialize(ISettingsService settings, WorkspaceTaskReader reader)
    {
        _settings = settings;
        _reader = reader;
    }

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (IsVisible && _settings is not null && _reader is not null)
            await ReloadAsync();
        else if (!IsVisible)
            _loadVersion++;
    }

    private async Task ReloadAsync()
    {
        var version = ++_loadVersion;
        IsBusy = true;
        LoadMessage = "Загружаем данные чатов…";
        try
        {
            var settings = await _settings!.LoadAsync();
            if (version != _loadVersion) return;

            _isRebuildingChatList = true;
            try
            {
                var previous = SelectedChat?.Chat;
                Chats.Clear();
                _taskCache.Clear();
                _totalDetected = 0;
                foreach (var chat in settings.Chats.Where(chat =>
                             chat.SynchronizationState == ChatSynchronizationState.COMPLETED))
                    Chats.Add(new WorkspaceChatRow(chat));
                SelectedChat = _page == WorkspacePage.Chat
                    ? Chats.FirstOrDefault(row => previous is not null &&
                        row.Chat.TelegramUserId == previous.TelegramUserId &&
                        row.Chat.PeerId == previous.PeerId &&
                        row.Chat.PeerType == previous.PeerType) ?? Chats.FirstOrDefault()
                    : null;
            }
            finally
            {
                _isRebuildingChatList = false;
            }

            Notify(nameof(ConnectedChatCount));
            foreach (var chat in Chats)
            {
                var detected = await _reader!.GetTasksAsync(chat.Chat);
                if (version != _loadVersion) return;
                _taskCache[chat] = detected;
                chat.DetectedCount = detected.Count;
                _totalDetected += detected.Count;
                Notify(nameof(TotalDetected));
                Notify(nameof(SelectedChatTaskCount));
            }

            UpdateSelectedTasks();
            LoadMessage = string.Empty;
        }
        catch (Exception)
        {
            if (version == _loadVersion)
            {
                LoadMessage = "Не удалось прочитать данные. Повторите вход в рабочую область.";
                TaskSummary = LoadMessage;
            }
        }
        finally
        {
            if (version == _loadVersion) IsBusy = false;
        }
    }

    private void UpdateSelectedTasks()
    {
        Tasks.Clear();
        if (SelectedChat is not null && _taskCache.TryGetValue(SelectedChat, out var detected))
        {
            foreach (var task in detected)
                Tasks.Add(new WorkspaceTaskCard(task));
        }
        TaskSummary = SelectedChat is null
            ? "Подключённых чатов пока нет."
            : $"Предварительно найдено заданий: {Tasks.Count}. Статусы выполнения ещё не подключены.";
        Notify(nameof(HasTasks));
        Notify(nameof(HasNoTasks));
        Notify(nameof(EmptyTasksMessage));
    }

    private void ChatList_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isRebuildingChatList || ChatList.SelectedItem is not WorkspaceChatRow row) return;
        SelectedChat = row;
        UpdateSelectedTasks();
        ShowChat();
    }

    private void OpenChatButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: WorkspaceChatRow row }) return;
        SelectedChat = row;
        _isRebuildingChatList = true;
        try { ChatList.SelectedItem = row; }
        finally { _isRebuildingChatList = false; }
        UpdateSelectedTasks();
        ShowChat();
    }

    private void ShowChat()
    {
        if (SelectedChat is null) return;
        _chatPage = ChatPage.Tasks;
        _showCompleted = false;
        _page = WorkspacePage.Chat;
        NotifyPageState();
    }

    private void HomeButton_OnClick(object sender, RoutedEventArgs args)
    {
        _page = WorkspacePage.Home;
        ChatList.SelectedItem = null;
        NotifyPageState();
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs args)
    {
        _page = WorkspacePage.Settings;
        ChatList.SelectedItem = null;
        NotifyPageState();
    }

    private void TasksTabButton_OnClick(object sender, RoutedEventArgs args)
    {
        _chatPage = ChatPage.Tasks;
        NotifyPageState();
    }

    private void SubscriptionsTabButton_OnClick(object sender, RoutedEventArgs args)
    {
        _chatPage = ChatPage.Subscriptions;
        NotifyPageState();
    }

    private void MembersTabButton_OnClick(object sender, RoutedEventArgs args)
    {
        _chatPage = ChatPage.Members;
        NotifyPageState();
    }

    private void DetectedFilterButton_OnClick(object sender, RoutedEventArgs args)
    {
        _showCompleted = false;
        NotifyTaskFilter();
    }

    private void CompletedFilterButton_OnClick(object sender, RoutedEventArgs args)
    {
        _showCompleted = true;
        NotifyTaskFilter();
    }

    private void ToggleSidebarButton_OnClick(object sender, RoutedEventArgs args)
    {
        _isSidebarExpanded = !_isSidebarExpanded;
        SidebarColumn.Width = new GridLength(_isSidebarExpanded ? 264 : 76);
        CollapseButton.Content = _isSidebarExpanded ? "‹" : "›";
        Notify(nameof(IsSidebarExpanded));
    }

    private void NotifyPageState()
    {
        Notify(nameof(IsHomeVisible));
        Notify(nameof(IsChatVisible));
        Notify(nameof(IsSettingsVisible));
        Notify(nameof(IsTasksVisible));
        Notify(nameof(IsSubscriptionsVisible));
        Notify(nameof(IsMembersVisible));
        NotifyTaskFilter();
        UpdateNavigationVisuals();
    }

    private void NotifyTaskFilter()
    {
        Notify(nameof(HasTasks));
        Notify(nameof(HasNoTasks));
        Notify(nameof(EmptyTasksMessage));
        if (DetectedFilterButton is not null && CompletedFilterButton is not null)
        {
            DetectedFilterButton.Opacity = _showCompleted ? 0.6 : 1;
            CompletedFilterButton.Opacity = _showCompleted ? 1 : 0.6;
        }
    }

    private void UpdateNavigationVisuals()
    {
        if (HomeButton is null || SettingsButton is null) return;
        HomeButton.Background = _page == WorkspacePage.Home
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 56, 157))
            : System.Windows.Media.Brushes.Transparent;
        SettingsButton.Background = _page == WorkspacePage.Settings
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 56, 157))
            : System.Windows.Media.Brushes.Transparent;
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

public sealed class WorkspaceChatRow(ConfiguredChat chat) : INotifyPropertyChanged
{
    private int _detectedCount;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ConfiguredChat Chat { get; } = chat;
    public string Title => Chat.DisplayName.Length > 0 ? Chat.DisplayName : Chat.TelegramTitle;
    public string Network => Chat.SocialNetwork;
    public string NetworkSymbol => string.Equals(Network, "Instagram", StringComparison.OrdinalIgnoreCase) ? "◎" : "➤";
    public int DetectedCount
    {
        get => _detectedCount;
        set
        {
            if (_detectedCount == value) return;
            _detectedCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedLabel)));
        }
    }
    public string DetectedLabel => $"Найдено заданий: {DetectedCount}";
}

public sealed class WorkspaceTaskCard(WorkspaceDetectedTask task)
{
    public string PostUrl => task.PostUrl;
    public string Title => "Задание из Telegram";
    public string Description { get; } = CreateDescription(task.Text);

    private static string CreateDescription(string text)
    {
        foreach (var part in text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            var cleaned = part.Trim();
            if (cleaned.Length == 0) continue;
            return cleaned.Length > 110 ? cleaned[..107] + "…" : cleaned;
        }
        return "Публикация из подключённого чата";
    }
}
