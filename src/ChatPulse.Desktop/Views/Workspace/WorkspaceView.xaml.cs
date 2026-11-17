using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Views.Dialogs;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Views.Workspace;

public partial class WorkspaceView : UserControl, INotifyPropertyChanged
{
    private enum WorkspacePage { Home, Chat, Settings }
    private enum ChatPage { Tasks, Subscriptions, Members }

    private ISettingsService? _settings;
    private WorkspaceTaskReader? _reader;
    private ChatSynchronizationService? _synchronization;
    private WorkspaceTaskSkipStore? _skipStore;
    private WorkspaceCompletedTaskHideStore? _hideStore;
    private WorkspaceChatRow? _selectedChat;
    private readonly Dictionary<WorkspaceChatRow, IReadOnlyList<WorkspaceDetectedTask>> _taskCache = [];
    private WorkspacePage _page = WorkspacePage.Home;
    private ChatPage _chatPage = ChatPage.Tasks;
    private string _taskSummary = string.Empty;
    private string _loadMessage = string.Empty;
    private string _reactionRefreshMessage = string.Empty;
    private bool _isBusy;
    private bool _isSidebarExpanded = true;
    private enum TaskFilter { Pending, Completed, Skipped }
    private TaskFilter _taskFilter;
    private bool _isRebuildingChatList;
    private int _loadVersion;
    private int _totalDetected;

    public WorkspaceView()
    {
        InitializeComponent();
        DataContext = this;
        CollectionViewSource.GetDefaultView(Tasks).GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(WorkspaceTaskCard.DateGroupLabel)));
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
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Notify(); Notify(nameof(HasTasks)); Notify(nameof(HasNoTasks)); Notify(nameof(IsNotBusy)); } }
    public bool HasTasks => !IsBusy && Tasks.Count > 0;
    public bool HasNoTasks => !IsBusy && Tasks.Count == 0;
    public bool IsNotBusy => !IsBusy;
    public string ReactionRefreshMessage
    {
        get => _reactionRefreshMessage;
        private set { _reactionRefreshMessage = value; Notify(); }
    }
    public string EmptyTasksMessage => _taskFilter switch
    {
        TaskFilter.Completed => "В этом чате нет заданий с подтверждённой твоей реакцией.",
        TaskFilter.Skipped => "В этом чате пока нет пропущенных сообщений.",
        _ => "Нет невыполненных заданий в загруженной истории."
    };
    public bool IsHomeVisible => _page == WorkspacePage.Home;
    public bool IsChatVisible => _page == WorkspacePage.Chat;
    public bool IsSettingsVisible => _page == WorkspacePage.Settings;
    public bool IsTasksVisible => _chatPage == ChatPage.Tasks;
    public bool IsSubscriptionsVisible => _chatPage == ChatPage.Subscriptions;
    public bool IsMembersVisible => _chatPage == ChatPage.Members;
    public bool IsSidebarExpanded => _isSidebarExpanded;

    public void Initialize(ISettingsService settings, WorkspaceTaskReader reader,
        ChatSynchronizationService synchronization)
    {
        _settings = settings;
        _reader = reader;
        _synchronization = synchronization;
        _skipStore = new WorkspaceTaskSkipStore(reader.DatabasePath);
        _hideStore = new WorkspaceCompletedTaskHideStore(reader.DatabasePath);
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
                chat.DetectedCount = detected.Count(task => !task.IsSkipped);
                _totalDetected += chat.DetectedCount;
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
            foreach (var task in detected.Where(task => _taskFilter switch
                     {
                         TaskFilter.Skipped => task.IsSkipped,
                         TaskFilter.Completed => !task.IsSkipped && task.IsCompleted,
                         _ => !task.IsSkipped && !task.IsCompleted
                     }))
                Tasks.Add(new WorkspaceTaskCard(task));
        }
        TaskSummary = SelectedChat is null
            ? "Подключённых чатов пока нет."
            : _taskFilter switch
            {
                TaskFilter.Skipped => $"Пропущенных: {Tasks.Count}. Реакции в Telegram не изменяются.",
                TaskFilter.Completed => $"Выполненных: {Tasks.Count}. Статус определяется по твоим реакциям в Telegram.",
                _ => $"Невыполненных: {Tasks.Count}. Статус определяется по твоим реакциям в Telegram."
            };
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
        _taskFilter = TaskFilter.Pending;
        UpdateSelectedTasks();
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
        _taskFilter = TaskFilter.Pending;
        UpdateSelectedTasks();
        NotifyTaskFilter();
    }

    private void CompletedFilterButton_OnClick(object sender, RoutedEventArgs args)
    {
        _taskFilter = TaskFilter.Completed;
        UpdateSelectedTasks();
        NotifyTaskFilter();
    }

    private void SkippedFilterButton_OnClick(object sender, RoutedEventArgs args)
    {
        _taskFilter = TaskFilter.Skipped;
        UpdateSelectedTasks();
        NotifyTaskFilter();
    }

    private async void CompleteTaskButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (IsBusy || _synchronization is null || SelectedChat is null ||
            sender is not Button { Tag: long messageId }) return;
        var row = SelectedChat;
        IsBusy = true;
        try
        {
            await _synchronization.CompleteTaskAsync(row.Chat, messageId);
            if (_taskCache.TryGetValue(row, out var tasks))
                _taskCache[row] = tasks.Select(task => task.MessageId == messageId
                    ? task with { IsCompleted = true } : task).ToArray();
            UpdateSelectedTasks();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Не удалось завершить задание: {exception.Message}",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            NotifyTaskFilter();
        }
    }

    private async void HideCompletedTaskButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (IsBusy || _hideStore is null || SelectedChat is null ||
            _taskFilter != TaskFilter.Completed ||
            sender is not Button { Tag: long messageId }) return;

        var row = SelectedChat;
        if (!_taskCache.TryGetValue(row, out var items) ||
            !items.Any(task => task.MessageId == messageId && task.IsCompleted && !task.IsSkipped)) return;

        var owner = Window.GetWindow(this);
        var dialog = new HideCompletedTaskDialog { Owner = owner };
        var mainWindow = owner as ChatPulse.Desktop.MainWindow;
        mainWindow?.ShowDialogOverlay();
        bool confirmed;
        try
        {
            confirmed = dialog.ShowDialog() == true;
        }
        finally
        {
            mainWindow?.HideDialogOverlay();
        }

        if (!confirmed) return;

        IsBusy = true;
        try
        {
            await _hideStore.HideAsync(row.Chat, messageId);
            _taskCache[row] = items.Where(task => task.MessageId != messageId).ToArray();
            row.DetectedCount = _taskCache[row].Count(task => !task.IsSkipped);
            _totalDetected = _taskCache.Sum(pair => pair.Value.Count(task => !task.IsSkipped));
            Notify(nameof(TotalDetected));
            Notify(nameof(SelectedChatTaskCount));
            UpdateSelectedTasks();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Не удалось удалить задание: {exception.Message}",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            NotifyTaskFilter();
        }
    }

    private async void SkipTaskButton_OnClick(object sender, RoutedEventArgs args)
        => await SetTaskSkippedAsync(sender, true);

    private async void RestoreTaskButton_OnClick(object sender, RoutedEventArgs args)
        => await SetTaskSkippedAsync(sender, false);

    private async Task SetTaskSkippedAsync(object sender, bool skipped)
    {
        if (IsBusy || _skipStore is null || SelectedChat is null ||
            sender is not Button { Tag: long messageId }) return;
        var row = SelectedChat;
        IsBusy = true;
        try
        {
            await _skipStore.SetSkippedAsync(row.Chat, messageId, skipped);
            if (_taskCache.TryGetValue(row, out var items))
            {
                _taskCache[row] = items.Select(task => task.MessageId == messageId
                    ? task with { IsSkipped = skipped } : task).ToArray();
                row.DetectedCount = _taskCache[row].Count(task => !task.IsSkipped);
                _totalDetected = _taskCache.Sum(pair => pair.Value.Count(task => !task.IsSkipped));
                Notify(nameof(TotalDetected));
                Notify(nameof(SelectedChatTaskCount));
            }
            UpdateSelectedTasks();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Не удалось изменить статус сообщения: {exception.Message}",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            NotifyTaskFilter();
        }
    }

    private async void CheckReactionsButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (IsBusy || _settings is null || _synchronization is null) return;
        IsBusy = true;
        ReactionRefreshMessage = "Синхронизируем новые сообщения и реакции…";
        try
        {
            var state = await _settings.LoadAsync();
            var chats = state.Chats.Where(chat =>
                chat.SynchronizationState == ChatSynchronizationState.COMPLETED).ToArray();
            var updated = 0;
            var errors = new List<string>();
            foreach (var chat in chats)
            {
                try
                {
                    updated += await _synchronization.SynchronizeLatestAsync(chat);
                }
                catch (Exception exception)
                {
                    errors.Add($"{chat.TelegramTitle}: {exception.Message}");
                }
            }
            await ReloadAsync();
            ReactionRefreshMessage = errors.Count == 0
                ? $"Синхронизация завершена. Новых сообщений: {updated}."
                : $"Не удалось синхронизировать часть чатов: {string.Join("; ", errors)}";
        }
        catch (Exception exception)
        {
            ReactionRefreshMessage = $"Не удалось синхронизировать: {exception.Message}";
        }
        finally { IsBusy = false; }
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
            DetectedFilterButton.Opacity = _taskFilter == TaskFilter.Pending ? 1 : 0.6;
            CompletedFilterButton.Opacity = _taskFilter == TaskFilter.Completed ? 1 : 0.6;
            if (SkippedFilterButton is not null)
                SkippedFilterButton.Opacity = _taskFilter == TaskFilter.Skipped ? 1 : 0.6;
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
            MessageBox.Show("Не удалось открыть ссылку.", "ChatPulse", MessageBoxButton.OK,
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
    private static readonly CultureInfo RUSSIAN_CULTURE = CultureInfo.GetCultureInfo("ru-RU");
    private DateTime? LocalDate => task.SentAtUtc?.ToLocalTime().DateTime;
    public string DateGroupLabel
    {
        get
        {
            if (LocalDate is not { } date) return "Дата неизвестна";
            var today = DateTime.Today;
            if (date.Date == today) return "Сегодня";
            if (date.Date == today.AddDays(-1)) return "Вчера";
            return date.ToString("d MMMM yyyy", RUSSIAN_CULTURE);
        }
    }
    public string TimeLabel => LocalDate?.ToString("HH:mm", RUSSIAN_CULTURE) ?? string.Empty;
    public long MessageId => task.MessageId;
    public bool IsSkipped => task.IsSkipped;
    public bool CanSkip => !task.IsSkipped && !task.IsCompleted;
    public bool CanHideCompleted => task.IsCompleted && !task.IsSkipped;
    public bool CanComplete => !task.IsSkipped && !task.IsCompleted;
    // Presentation-only transformation: the original Telegram text and emoji offsets remain in SQLite.
    private readonly WorkspaceTaskPresentation _presentation = WorkspaceTaskPresentation.Create(task.Text, task.CustomEmojiJson);
    public string CustomEmojiJson => _presentation.CustomEmojiJson;
    public string ReelUrl => _presentation.ReelUrl;
    public string FeedPostUrl => _presentation.FeedPostUrl;
    public bool HasReelUrl => !string.IsNullOrWhiteSpace(ReelUrl);
    public bool HasFeedPostUrl => !string.IsNullOrWhiteSpace(FeedPostUrl);
    public bool HasBothInstagramLinks => HasReelUrl && HasFeedPostUrl;
    public string PostUrl => task.PostUrl;
    public bool HasPostUrl => !string.IsNullOrWhiteSpace(task.PostUrl);
    public bool HasStandalonePostUrl => HasPostUrl && !HasReelUrl && !HasFeedPostUrl;
    public string SourceUrl => task.SourceUrl;
    public bool HasSourceUrl => !string.IsNullOrWhiteSpace(task.SourceUrl);
    public string Title => string.IsNullOrWhiteSpace(task.AuthorName) ? "Автор неизвестен" : task.AuthorName;
    public string Description => string.IsNullOrWhiteSpace(_presentation.Text)
        ? "Сообщение без текста (возможно, содержит медиа)" : _presentation.Text;
}
