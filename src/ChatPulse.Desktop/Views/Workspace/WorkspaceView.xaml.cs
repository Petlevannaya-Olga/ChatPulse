using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    private readonly InstagramBrowserService _instagramBrowser = new();
    private bool _instagramSettingsLoaded;
    private bool _instagramSessionCleared;
    private bool _instagramBusy;
    private CancellationTokenSource? _instagramBatchCancellation;
    private InstagramParticipantOption? _selectedInstagramParticipant;
    private string _instagramStatus = "Аккаунт не подключён";
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
        System.Windows.Application.Current.Exit += (_, _) =>
        {
            _instagramBatchCancellation?.Cancel();
            _instagramBrowser.Dispose();
        };
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
    public string InstagramStatus
    {
        get => _instagramStatus;
        private set { _instagramStatus = value; Notify(); }
    }

    public ObservableCollection<InstagramParticipantOption> InstagramParticipants { get; } = [];
    public ObservableCollection<InstagramCheckDisplayRow> InstagramCheckResults { get; } = [];
    public bool CanCancelInstagramBatch => _instagramBatchCancellation is not null;

    public InstagramParticipantOption? SelectedInstagramParticipant
    {
        get => _selectedInstagramParticipant;
        set { _selectedInstagramParticipant = value; Notify(); }
    }

    public bool CanUseInstagram => !_instagramBusy;

    private void SetInstagramBusy(bool busy)
    {
        _instagramBusy = busy;
        Notify(nameof(CanUseInstagram));
    }

    private async Task LoadInstagramSettingsAsync()
    {
        if (_settings is null) return;
        var saved = await _settings.LoadAsync();
        InstagramTargetBox.Text = saved.InstagramTargetUserName ?? string.Empty;
        InstagramBrowserBox.Text = saved.InstagramBrowserUserName ?? string.Empty;
        _instagramSettingsLoaded = true;
        InstagramStatus = string.IsNullOrWhiteSpace(saved.InstagramBrowserUserName)
            ? "Укажите аккаунты и сохраните настройки."
            : "Настройки сохранены. Состояние сессии ещё не проверено.";
    }

    private async Task LoadInstagramParticipantsAsync()
    {
        if (_settings is null || _reader is null) return;
        var state = await _settings.LoadAsync();
        var lookup = new InstagramParticipantLookup(_reader.DatabasePath);
        var allCurrent = await lookup.ReadAllCurrentAsync(state.Chats);
        var previouslySelected = SelectedInstagramParticipant?.ParticipantId;
        InstagramParticipants.Clear();
        foreach (var participant in allCurrent.Where(item => !string.IsNullOrWhiteSpace(item.UserName)))
            InstagramParticipants.Add(participant);
        SelectedInstagramParticipant = InstagramParticipants.FirstOrDefault(item =>
            item.ParticipantId == previouslySelected) ?? InstagramParticipants.FirstOrDefault();

        var plan = InstagramBatchEligibility.Prepare(allCurrent);
        InstagramCheckResults.Clear();
        foreach (var name in plan.UserNames)
            InstagramCheckResults.Add(new InstagramCheckDisplayRow(name, "—", "—", ""));
        if (string.IsNullOrWhiteSpace(state.InstagramTargetUserName)) return;
        var store = new InstagramSubscriptionCheckStore(_reader.DatabasePath);
        foreach (var saved in store.Read(state.InstagramTargetUserName, plan.UserNames))
        {
            var index = FindInstagramCheckRow(saved.UserName);
            if (index < 0) continue;
            var row = InstagramCheckResults[index];
            InstagramCheckResults[index] = saved.Kind == "followers"
                ? row with { FollowsMe = DescribeInstagramStatus(saved.Status), LastCheck = DisplayInstagramCheckTime(saved.CheckedAtUtc) }
                : row with { IFollow = DescribeInstagramStatus(saved.Status), LastCheck = DisplayInstagramCheckTime(saved.CheckedAtUtc) };
        }
    }

    private int FindInstagramCheckRow(string userName)
    {
        for (var index = 0; index < InstagramCheckResults.Count; index++)
            if (string.Equals(InstagramCheckResults[index].UserName, userName,
                    StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private static string DisplayInstagramCheckTime(string isoTimestamp)
        => DateTimeOffset.TryParse(isoTimestamp, out var date)
            ? date.ToLocalTime().ToString("dd.MM HH:mm") : isoTimestamp;

    private static string DescribeInstagramStatus(string status) => status switch
    {
        "FOUND" => "Найдена",
        "NOT_FOUND" => "Не найдена в поиске",
        "INCONCLUSIVE" => "Не удалось проверить",
        _ => "—"
    };

    private void CancelInstagramBatchButton_OnClick(object sender, RoutedEventArgs args)
        => _instagramBatchCancellation?.Cancel();

    private async void CheckAllInstagramParticipantsButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_settings is null || _reader is null || _instagramBusy) return;
        SetInstagramBusy(true);
        using var cancellation = new CancellationTokenSource();
        _instagramBatchCancellation = cancellation;
        Notify(nameof(CanCancelInstagramBatch));
        var completed = 0;
        var found = 0;
        var notFound = 0;
        var inconclusive = 0;
        try
        {
            var settings = await _settings.LoadAsync();
            if (string.IsNullOrWhiteSpace(settings.InstagramTargetUserName) ||
                string.IsNullOrWhiteSpace(settings.InstagramBrowserUserName))
            {
                InstagramStatus = "Сначала сохраните основной и проверочный Instagram-аккаунты.";
                return;
            }
            var lookup = new InstagramParticipantLookup(_reader.DatabasePath);
            var allCurrent = await lookup.ReadAllCurrentAsync(settings.Chats, cancellation.Token);
            var plan = InstagramBatchEligibility.Prepare(allCurrent);
            if (plan.UserNames.Count == 0)
            {
                InstagramStatus = "Нет актуальных участниц для проверки: VIP и администрация исключены; " +
                                  "необходима корректная Instagram-ссылка аккаунта активности.";
                return;
            }

            InstagramCheckResults.Clear();
            foreach (var userName in plan.UserNames)
                InstagramCheckResults.Add(new InstagramCheckDisplayRow(userName, "—", "—", ""));
            var store = new InstagramSubscriptionCheckStore(_reader.DatabasePath);
            var total = plan.UserNames.Count * 2;
            InstagramStatus = $"Проверяем {plan.UserNames.Count} уникальных аккаунтов активности. " +
                              $"Исключено VIP/администрация: {plan.ExcludedCount}; " +
                              $"без подходящей ссылки: {plan.MissingActivityProfileCount}; " +
                              $"дубликатов: {plan.DuplicateCount}. Не закрывайте Chrome.";
            var result = await _instagramBrowser.CheckParticipantsAsync(
                settings.InstagramTargetUserName, plan.UserNames, progress =>
                {
                    // This callback runs on the Selenium worker. Each completed direction
                    // is committed before reporting progress to the UI thread.
                    store.Save(settings.InstagramTargetUserName,
                        settings.InstagramBrowserUserName, progress);
                    completed = progress.Completed;
                    switch (progress.Result.Status)
                    {
                        case InstagramSearchStatus.FOUND: found++; break;
                        case InstagramSearchStatus.NOT_FOUND: notFound++; break;
                        default: inconclusive++; break;
                    }
                    // Non-blocking UI dispatch prevents shutdown deadlocks while Chrome
                    // is being closed. Persistence has already finished at this point.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var index = FindInstagramCheckRow(progress.UserName);
                        if (index >= 0)
                        {
                            var row = InstagramCheckResults[index];
                            var label = DescribeInstagramStatus(progress.Result.Status.ToString());
                            InstagramCheckResults[index] = progress.Kind == "followers"
                                ? row with { FollowsMe = label, LastCheck = DateTime.Now.ToString("dd.MM HH:mm") }
                                : row with { IFollow = label, LastCheck = DateTime.Now.ToString("dd.MM HH:mm") };
                        }
                        var direction = progress.Kind == "followers" ? "Подписчики" : "Подписки";
                        if (_instagramBatchCancellation is not null)
                            InstagramStatus = $"Проверено {progress.Completed} из {total}: @{progress.UserName} ({direction}). " +
                                              $"Найдено: {found}; нет в поиске: {notFound}; ошибок: {inconclusive}.";
                    }));
                }, cancellation.Token);
            var ending = result.StopReason is null ? "Проверка завершена." :
                $"Проверка остановлена: {result.StopReason}";
            InstagramStatus = $"{ending} Выполнено {completed} из {total} поисков. " +
                              $"Найдено: {found}; не найдено в поиске: {notFound}; " +
                              $"не удалось проверить: {inconclusive}. " +
                              $"Исключено VIP/администрация: {plan.ExcludedCount}; " +
                              $"без ссылки активности: {plan.MissingActivityProfileCount}. " +
                              "Отсутствие в поиске не является доказательством отписки.";
        }
        catch (OperationCanceledException)
        {
            InstagramStatus = $"Проверка остановлена пользователем. Сохранено {completed} поисков. " +
                              "Остальные участницы не проверены.";
        }
        catch (Exception exception)
        {
            InstagramStatus = $"Массовая проверка прервана после {completed} поисков: {exception.Message}. " +
                              "Уже выполненные проверки сохранены.";
        }
        finally
        {
            _instagramBatchCancellation = null;
            Notify(nameof(CanCancelInstagramBatch));
            SetInstagramBusy(false);
        }
    }

    private async void CheckInstagramParticipantButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_settings is null || _instagramBusy) return;
        SetInstagramBusy(true);
        try
        {
            // Refresh before checking, so removed participants are not checked as current.
            await LoadInstagramParticipantsAsync();
            var participant = SelectedInstagramParticipant;
            if (participant is null)
            {
                InstagramStatus = "Нет участниц с Instagram-ссылкой активности в актуальных списках.";
                return;
            }
            var saved = await _settings.LoadAsync();
            if (string.IsNullOrWhiteSpace(saved.InstagramTargetUserName))
            {
                InstagramStatus = "Сначала сохраните основной Instagram-профиль.";
                return;
            }
            InstagramStatus = $"Ищем @{participant.UserName} в подписчиках и подписках " +
                              $"@{saved.InstagramTargetUserName}. Не закрывайте Chrome.";
            var result = await _instagramBrowser.CheckParticipantAsync(
                saved.InstagramTargetUserName, participant.ActivityProfileUrl);
            InstagramStatus = $"Проверка @{result.UserName} (аккаунт активности)\n" +
                              $"Участница → я: {result.FollowsMe.Describe()}\n" +
                              $"Я → участница: {result.IFollow.Describe()}";
        }
        catch (Exception exception)
        {
            InstagramStatus = $"Проверка участницы не удалась: {exception.Message}";
        }
        finally { SetInstagramBusy(false); }
    }

    private async void SaveInstagramButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_settings is null || _instagramBusy) return;
        SetInstagramBusy(true);
        try
        {
            var target = InstagramUserName.Normalize(InstagramTargetBox.Text);
            var browser = InstagramUserName.Normalize(InstagramBrowserBox.Text);
            var previous = await _settings.LoadAsync();
            if (previous.InstagramBrowserUserName is { } old &&
                !string.Equals(old, browser, StringComparison.OrdinalIgnoreCase) && !_instagramSessionCleared)
            {
                InstagramStatus = "Для смены проверочного аккаунта сначала сбросьте текущую сессию.";
                return;
            }
            await _settings.SaveInstagramAccountsAsync(target, browser);
            _instagramSessionCleared = false;
            InstagramStatus = "Аккаунты сохранены. Теперь откройте Instagram для входа.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            InstagramStatus = exception.Message;
        }
        finally { SetInstagramBusy(false); }
    }

    private async void InstagramLoginButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_settings is null || _instagramBusy) return;
        SetInstagramBusy(true);
        try
        {
            var saved = await _settings.LoadAsync();
            if (string.IsNullOrWhiteSpace(saved.InstagramBrowserUserName))
            {
                InstagramStatus = "Сначала сохраните основной и проверочный аккаунты.";
                return;
            }
            await _instagramBrowser.OpenLoginAsync();
            InstagramStatus = "Chrome открыт. Войдите вручную и нажмите «Проверить сессию».";
        }
        catch (Exception exception)
        {
            InstagramStatus = $"Не удалось запустить Chrome: {exception.Message}";
        }
        finally { SetInstagramBusy(false); }
    }

    private async void CheckInstagramButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_instagramBusy) return;
        SetInstagramBusy(true);
        try
        {
            var active = await _instagramBrowser.HasSessionAsync();
            InstagramStatus = active
                ? "Cookie сессии обнаружен. Принадлежность аккаунту и доступ к спискам пока не подтверждены."
                : "Сессия не обнаружена. Откройте Instagram и авторизуйтесь вручную.";
        }
        catch (Exception exception)
        {
            InstagramStatus = $"Проверка сессии не удалась: {exception.Message}";
        }
        finally { SetInstagramBusy(false); }
    }

    private async void CollectInstagramListsButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_settings is null || _instagramBusy) return;
        SetInstagramBusy(true);
        try
        {
            var settings = await _settings.LoadAsync();
            if (string.IsNullOrWhiteSpace(settings.InstagramTargetUserName))
            {
                InstagramStatus = "Сначала сохраните основной Instagram-профиль.";
                return;
            }
            InstagramStatus = "Прокручивайте список подписчиков вручную в Chrome до конца. " +
                              "Затем не прокручивайте 8 секунд. Selenium только считывает имена (не более 3 минут).";
            var followers = await _instagramBrowser.CollectFollowersAsync(settings.InstagramTargetUserName);
            InstagramStatus = $"Считано уникальных имён подписчиков: {followers.UserNames.Count}.\n" +
                              followers.Describe("Подписчики") +
                              (string.IsNullOrWhiteSpace(followers.Diagnostics)
                                  ? string.Empty : $"\n{followers.Diagnostics}") +
                              "\nЭто диагностический результат: проверка подписок пока не выполняется.";
        }
        catch (Exception exception)
        {
            InstagramStatus = $"Не удалось получить списки Instagram: {exception.Message}";
        }
        finally { SetInstagramBusy(false); }
    }

    private async void ResetInstagramButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_instagramBusy || _settings is null) return;
        if (MessageBox.Show("Удалить локальную сессию Instagram? Для нового аккаунта потребуется повторный вход.",
                "ChatPulse — Instagram", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        SetInstagramBusy(true);
        try
        {
            await _instagramBrowser.ClearSessionAsync();
            _instagramSessionCleared = true;
            InstagramStatus = "Сессия очищена. Можно изменить проверочный аккаунт и сохранить настройки.";
        }
        catch (Exception exception)
        {
            InstagramStatus = $"Не удалось очистить сессию: {exception.Message}";
        }
        finally { SetInstagramBusy(false); }
    }

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
                     }).OrderByDescending(task => task.MessageId))
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

    private async void SettingsButton_OnClick(object sender, RoutedEventArgs args)
    {
        _page = WorkspacePage.Settings;
        ChatList.SelectedItem = null;
        NotifyPageState();
        try
        {
            if (!_instagramSettingsLoaded) await LoadInstagramSettingsAsync();
            await LoadInstagramParticipantsAsync();
        }
        catch (Exception exception)
        {
            InstagramStatus = $"Не удалось загрузить настройки Instagram: {exception.Message}";
        }
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

public sealed record InstagramCheckDisplayRow(string UserName, string FollowsMe,
    string IFollow, string LastCheck);
