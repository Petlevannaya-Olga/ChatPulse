using ChatPulse.Application.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Views.Dialogs;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Views.Workspace;

public partial class WorkspaceView : UserControl, INotifyPropertyChanged
{
    private enum WorkspacePage { Home, Chat, Settings }

    private enum ChatPage { Tasks, Subscriptions, MyActivity }

    private ISettingsService? _settings;
    // Legacy Chrome directory is retained for the reserve account: no automatic cookie migration.
    private readonly InstagramBrowserService _instagramBrowser = new();
    private readonly InstagramBrowserService _mainInstagramBrowser = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatPulse", "InstagramBrowserMain"));
    private bool _instagramSettingsLoaded;
    private bool _instagramSessionCleared;
    private bool _mainInstagramSessionCleared;
    private bool _instagramBusy;
    private CancellationTokenSource? _instagramBatchCancellation;
    private InstagramParticipantOption? _selectedInstagramParticipant;
    private string _instagramStatus = "Аккаунт не подключён";
    private WorkspaceTaskReader? _reader;
    private ChatSynchronizationService? _synchronization;
    private InstagramChatConnectionService? _instagramChatConnection;
    private ITelegramParticipantListHistoryReader? _participantHistoryReader;
    private CancellationTokenSource? _historyImportCancellation;
    private bool _historyImportRunning;
    private WorkspaceChatRow? _selectedHistoryChat;
    private string _participantHistoryLastReport = string.Empty;
    private WorkspaceTaskSkipStore? _skipStore;
    private WorkspaceCompletedTaskHideStore? _hideStore;
    private WorkspaceChatRow? _selectedChat;
    private readonly Dictionary<WorkspaceChatRow, IReadOnlyList<WorkspaceDetectedTask>> _taskCache = [];
    private WorkspacePage _page = WorkspacePage.Home;
    private ChatPage _chatPage = ChatPage.Tasks;
    private string _taskSummary = string.Empty;
    private string _loadMessage = string.Empty;
    private string _reactionRefreshMessage = string.Empty;
    private string _lastAllChatsSyncLabel = "Последнее обновление: —";
    private string _lastAllChatsSyncNewMessagesLabel = "Новых сообщений: —";
    private string _lastSelectedChatSyncLabel = "Последняя: ещё не выполнялась";
    private bool _isBusy;
    private bool _isSidebarExpanded = true;
    private bool _isSubscriptionTableExpanded;
    private bool _isTaskListExpanded;
    private static readonly string[] SubscriptionColumnKeys =
        ["participant", "profile", "i-follow", "follows-me", "updated"];
    private readonly DispatcherTimer _subscriptionColumnSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(450)
    };
    private bool _subscriptionColumnsRestored;
    private bool _restoringSubscriptionColumnWidths;

    private enum TaskFilter { Pending, Completed, Skipped }

    private TaskFilter _taskFilter;
    private bool _oldestTasksFirst;
    private const int TASK_PAGE_SIZE = 30;
    private const int TASK_MAX_CACHED_PAGES = 9;
    private readonly SortedDictionary<int, IReadOnlyList<WorkspaceDetectedTask>> _taskPages = new();
    private int _taskWindowVersion;
    private CancellationTokenSource? _taskWindowCancellation;
    private int _taskTotalCount;
    private bool _isTaskPageLoading;
    private bool _isRenderingTasks;
    private string _taskPageError = string.Empty;
    private int? _failedTaskPage;
    private bool _failedTaskPagePrepend;
    private bool _isRebuildingChatList;
    private readonly ICollectionView _subscriptionRowsView;
    private bool _showOnlySubscriptionDiscrepancies;
    private InstagramParticipantCategoryCounts? _subscriptionTileCounts;
    private int _subscriptionTileLoadVersion;
    private int _subscriptionRowsLoadVersion;
    private string _subscriptionQuery = string.Empty;
    private string _subscriptionsSummary = "Выберите чат для просмотра участниц.";
    private string _subscriptionsSyncMessage = string.Empty;
    private string _participantListMessageDateLabel = string.Empty;
    private string _subscriptionsCheckMessage = string.Empty;
    private InstagramBatchResultSummary? _latestSubscriptionBatch;
    private bool _isSubscriptionReviewRequired;
    private ParticipantListSourceInfo? _latestParticipantListSource;
    private ParticipantListRefreshAttempt? _latestParticipantListAttempt;
    private int _loadVersion;
    private int _totalDetected;

    private void SubscriptionTableSurface_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid surface)
            return;

        var width = e.NewSize.Width;
        var height = e.NewSize.Height;
        if (width <= 0 || height <= 0)
        {
            surface.Clip = Geometry.Empty;
            return;
        }

        const double radius = 11;
        surface.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    public WorkspaceView()
    {
        InitializeComponent();
        _subscriptionColumnSaveTimer.Tick += SubscriptionColumnSaveTimer_OnTick;
        var widthDescriptor = DependencyPropertyDescriptor.FromProperty(
            DataGridColumn.WidthProperty, typeof(DataGridColumn));
        foreach (var column in SubscriptionDataGrid.Columns)
            widthDescriptor?.AddValueChanged(column, SubscriptionColumnWidth_OnChanged);
        _subscriptionRowsView = CollectionViewSource.GetDefaultView(SubscriptionRows);
        _subscriptionRowsView.Filter = FilterSubscriptionRow;
        DataContext = this;
        CollectionViewSource.GetDefaultView(Tasks).GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(WorkspaceTaskCard.DateGroupLabel)));
        UpdateNavigationVisuals();
        System.Windows.Application.Current.Exit += (_, _) =>
        {
            _instagramBatchCancellation?.Cancel();
            _historyImportCancellation?.Cancel();
            _taskWindowCancellation?.Cancel();
            _taskWindowCancellation?.Dispose();
            _instagramBrowser.Dispose();
            _mainInstagramBrowser.Dispose();
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
            // A previously started load must never publish rows for another chat.
            _subscriptionRowsLoadVersion++;
            SetLatestParticipantListSource(null);
            Notify();
            Notify(nameof(SelectedChatTitle));
            Notify(nameof(SelectedChatNetwork));
            Notify(nameof(SelectedChatTaskCount));
            Notify(nameof(IsCompletedTasksSelected));
            Notify(nameof(IsPendingTasksSelected));
            Notify(nameof(LastSelectedChatSyncLabel));
        }
    }

    public string SelectedChatTitle => SelectedChat?.Title ?? "Выберите чат";
    public string SelectedChatNetwork => SelectedChat?.Network ?? string.Empty;
    public string SelectedChatTaskCount => SelectedChat is null ? "—" : SelectedChat.DetectedCount.ToString();
    public string TotalDetected => _totalDetected.ToString();
    public string ConnectedChatCount => Chats.Count.ToString();


    public string LastAllChatsSyncLabel
    {
        get => _lastAllChatsSyncLabel;
        private set { _lastAllChatsSyncLabel = value; Notify(); }
    }
    public string LastAllChatsSyncNewMessagesLabel
    {
        get => _lastAllChatsSyncNewMessagesLabel;
        private set { _lastAllChatsSyncNewMessagesLabel = value; Notify(); }
    }
    public string LastSelectedChatSyncLabel
    {
        get => _lastSelectedChatSyncLabel;
        private set { _lastSelectedChatSyncLabel = value; Notify(); }
    }

    private static string FormatSyncStamp(DateTimeOffset? timestamp)
        => timestamp is { } value
            ? $"Последняя: {value.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "Последняя: ещё не выполнялась";

    private async Task LoadSynchronizationStampsAsync()
    {
        if (_reader is null) return;
        var owner = Chats.FirstOrDefault()?.Chat.TelegramUserId;
        var selected = SelectedChat;
        var store = new WorkspaceSynchronizationStampStore(_reader.DatabasePath);
        try
        {
            var all = owner.HasValue ? await store.ReadAllSummaryAsync(owner.Value) : (CompletedAt: (DateTimeOffset?)null, NewMessages: (int?)null);
            var chatStamp = selected is not null ? await store.ReadChatAsync(selected.Chat) : null;
            if (owner == Chats.FirstOrDefault()?.Chat.TelegramUserId)
            {
                LastAllChatsSyncLabel = all.CompletedAt is { } completed
                    ? $"Последнее обновление: {completed.ToLocalTime():dd.MM.yyyy}"
                    : "Последнее обновление: —";
                LastAllChatsSyncNewMessagesLabel = all.NewMessages is { } count
                    ? $"Новых сообщений: {count}"
                    : "Новых сообщений: —";
            }
            if (ReferenceEquals(selected, SelectedChat))
                LastSelectedChatSyncLabel = FormatSyncStamp(chatStamp);
        }
        catch (Exception)
        {
            // An optional timestamp must never block task browsing or change sync state.
            if (ReferenceEquals(selected, SelectedChat))
                LastSelectedChatSyncLabel = "Последняя: не удалось прочитать";
        }
    }

    public string TaskSummary
    {
        get => _taskSummary;
        private set
        {
            _taskSummary = value;
            Notify();
        }
    }

    public string LoadMessage
    {
        get => _loadMessage;
        private set
        {
            _loadMessage = value;
            Notify();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            Notify();
            Notify(nameof(HasTasks));
            Notify(nameof(HasNoTasks));
            Notify(nameof(IsNotBusy));
            Notify(nameof(CanRebuildParticipantHistory));
        }
    }

    public WorkspaceChatRow? SelectedHistoryChat
    {
        get => _selectedHistoryChat;
        set
        {
            if (ReferenceEquals(_selectedHistoryChat, value)) return;
            _selectedHistoryChat = value;
            Notify();
            Notify(nameof(CanRebuildParticipantHistory));
            Notify(nameof(ParticipantHistoryRulesHint));
        }
    }

    public bool CanRebuildParticipantHistory => !IsBusy && !_instagramBusy &&
        !_historyImportRunning && _participantHistoryReader is not null &&
        ChatParticipantHistoryRuleSelection.SupportsChat14(SelectedHistoryChat?.Chat);

    public string ParticipantHistoryRulesHint => SelectedHistoryChat is null
        ? "Выберите подключённый чат."
        : ChatParticipantHistoryRuleSelection.SupportsChat14(SelectedHistoryChat.Chat)
            ? "Для этого чата настроены правила 14-го чата. Будут проверены все доступные списки Telegram."
            : "Исторический парсер для выбранного чата пока не настроен. " +
              "Правила 14-го чата не будут применяться к чужим спискам.";

    public string ParticipantHistoryLastReport
    {
        get => _participantHistoryLastReport;
        private set { _participantHistoryLastReport = value; Notify(); }
    }

    public bool HasTasks => !IsBusy && Tasks.Count > 0;
    public bool HasNoTasks => !IsBusy && !_isTaskPageLoading && Tasks.Count == 0;
    public bool IsTaskPageLoading => _isTaskPageLoading;
    public string TaskPageError => _taskPageError;
    public bool HasTaskPageError => !string.IsNullOrEmpty(_taskPageError);
    public bool IsNotBusy => !IsBusy;

    public string ReactionRefreshMessage
    {
        get => _reactionRefreshMessage;
        private set
        {
            _reactionRefreshMessage = value;
            Notify();
        }
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
        private set
        {
            _instagramStatus = value;
            Notify();
        }
    }

    public ObservableCollection<InstagramParticipantOption> InstagramParticipants { get; } = [];
    public ObservableCollection<InstagramSubscriptionRow> SubscriptionRows { get; } = [];
    public ICollectionView SubscriptionRowsView => _subscriptionRowsView;
    public string SubscriptionCountLabel => FormatSubscriptionCount(_subscriptionTileCounts?.Total);

    public string SubscriptionAdministrationCountLabel =>
        FormatSubscriptionCount(_subscriptionTileCounts?.Administration);

    public string SubscriptionVipCountLabel => FormatSubscriptionCount(_subscriptionTileCounts?.Vip);
    public string SubscriptionRegularCountLabel => FormatSubscriptionCount(_subscriptionTileCounts?.Regular);

    private static string FormatSubscriptionCount(int? count) =>
        count?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private void SetSubscriptionTileCounts(InstagramParticipantCategoryCounts? counts)
    {
        _subscriptionTileCounts = counts;
        Notify(nameof(SubscriptionCountLabel));
        Notify(nameof(SubscriptionAdministrationCountLabel));
        Notify(nameof(SubscriptionVipCountLabel));
        Notify(nameof(SubscriptionRegularCountLabel));
    }

    // The tile updates when a chat opens, without requiring the Subscriptions tab to be visited.
    private async Task RefreshSubscriptionTileCountsAsync(WorkspaceChatRow? chat)
    {
        if (chat is null || _settings is null || _reader is null) return;
        var request = ++_subscriptionTileLoadVersion;
        try
        {
            var lookup = new InstagramParticipantLookup(_reader.DatabasePath);
            var participants = await lookup.ReadAllCurrentAsync([chat.Chat]);
            // The participant-list tile shows the full Telegram register. Own accounts
            // are excluded from subscription checks, but remain part of its headcount.
            if (request == _subscriptionTileLoadVersion && ReferenceEquals(chat, SelectedChat))
                SetSubscriptionTileCounts(InstagramParticipantCategoryCounts.FromRegister(participants));
        }
        catch (Exception)
        {
            // A failed background count must not interrupt tasks or replace the chat register.
            if (request == _subscriptionTileLoadVersion && ReferenceEquals(chat, SelectedChat))
                SetSubscriptionTileCounts(null);
        }
    }

    public string SubscriptionsSummary
    {
        get => _subscriptionsSummary;
        private set
        {
            _subscriptionsSummary = value;
            Notify();
        }
    }

    public string ParticipantListMessageDateLabel
    {
        get => _participantListMessageDateLabel;
        private set
        {
            _participantListMessageDateLabel = value;
            Notify();
        }
    }

    public string SubscriptionsSyncMessage
    {
        get => _subscriptionsSyncMessage;
        private set
        {
            _subscriptionsSyncMessage = value;
            Notify();
        }
    }

    public string SubscriptionsCheckMessage
    {
        get => _subscriptionsCheckMessage;
        private set
        {
            _subscriptionsCheckMessage = value;
            Notify();
            InstagramStatus = value;
        }
    }

    public InstagramBatchResultSummary? LatestSubscriptionBatch
    {
        get => _latestSubscriptionBatch;
        private set
        {
            _latestSubscriptionBatch = value;
            Notify();
            Notify(nameof(HasLatestSubscriptionBatch));
            Notify(nameof(LatestCheckDateLabel));
            Notify(nameof(LatestCheckCountsLabel));
        }
    }

    public bool HasLatestSubscriptionBatch => LatestSubscriptionBatch is not null;

    public bool IsSubscriptionReviewRequired
    {
        get => _isSubscriptionReviewRequired;
        private set
        {
            if (_isSubscriptionReviewRequired == value) return;
            _isSubscriptionReviewRequired = value;
            Notify();
        }
    }

    public string LatestCheckDateLabel => LatestSubscriptionBatch is { } summary
        ? $"Последняя успешная: {summary.FinishedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}"
        : "Успешных проверок ещё нет";

    public string LatestCheckCountsLabel => LatestSubscriptionBatch is { } summary
        ? $"Найдено: {summary.Found} · Не найдено: {summary.NotFound} · Ошибок: {summary.Errors}"
        : "Нажмите для просмотра подробностей";

    public string ParticipantLastSyncLabel => _latestParticipantListSource is { LastSuccessfulSyncAtUtc: { } synced }
        ? $"Синхронизация: {synced.ToLocalTime():dd.MM.yyyy HH:mm}"
        : "Дата синхронизации неизвестна";

    private void SetLatestParticipantListSource(ParticipantListSourceInfo? source)
    {
        _latestParticipantListSource = source;
        ParticipantListMessageDateLabel = source is null
            ? "Список ещё не загружен"
            : source.SentAtUtc is null
                ? "Дата списка неизвестна"
                : $"Список от {source.DateLabel}";
        Notify(nameof(ParticipantLastSyncLabel));
    }

    public bool ShowOnlySubscriptionDiscrepancies
    {
        get => _showOnlySubscriptionDiscrepancies;
        set
        {
            if (_showOnlySubscriptionDiscrepancies == value) return;
            _showOnlySubscriptionDiscrepancies = value;
            Notify();
            _subscriptionRowsView.Refresh();
        }
    }

    public string SubscriptionQuery
    {
        get => _subscriptionQuery;
        set
        {
            if (_subscriptionQuery == value) return;
            _subscriptionQuery = value;
            Notify();
            _subscriptionRowsView.Refresh();
        }
    }

    public bool CanCancelInstagramBatch => _instagramBatchCancellation is not null;

    public InstagramParticipantOption? SelectedInstagramParticipant
    {
        get => _selectedInstagramParticipant;
        set
        {
            _selectedInstagramParticipant = value;
            Notify();
        }
    }

    public bool CanUseInstagram => !_instagramBusy;

    private void SetInstagramBusy(bool busy)
    {
        _instagramBusy = busy;
        Notify(nameof(CanUseInstagram));
        Notify(nameof(CanRebuildParticipantHistory));
    }

    private async Task LoadInstagramSettingsAsync()
    {
        if (_settings is null) return;
        var saved = await _settings.LoadAsync();
        var owned = _reader is null
            ? null
            : await new InstagramOwnAccountStore(_reader.DatabasePath)
                .EnsureFromLegacyAsync(saved.InstagramTargetUserName);
        InstagramTargetBox.Text = owned?.ReceivingUserName ?? saved.InstagramTargetUserName ?? string.Empty;
        InstagramGiveFromBox.Text = owned?.GivingUserName ??
            (string.Equals(saved.InstagramTargetUserName, saved.InstagramBrowserUserName,
                StringComparison.OrdinalIgnoreCase) ? string.Empty : saved.InstagramBrowserUserName) ?? string.Empty;
        _instagramSettingsLoaded = true;
        InstagramStatus = string.IsNullOrWhiteSpace(InstagramGiveFromBox.Text)
            ? "Укажите основной и резервный аккаунты, затем сохраните настройки."
            : "Два аккаунта настроены. Их Chrome-сессии проверяются отдельно.";
    }

    private async Task LoadInstagramParticipantsAsync()
    {
        if (_settings is null || _reader is null) return;
        var state = await _settings.LoadAsync();
        var lookup = new InstagramParticipantLookup(_reader.DatabasePath);
        var allCurrent = await lookup.ReadAllCurrentAsync(state.Chats);
        var owned = await new InstagramOwnAccountStore(_reader.DatabasePath)
            .EnsureFromLegacyAsync(state.InstagramTargetUserName);
        var ownNames = InstagramOwnAccountFilter.GetUserNames(owned, state.InstagramBrowserUserName);
        var previouslySelected = SelectedInstagramParticipant?.ParticipantId;
        InstagramParticipants.Clear();
        foreach (var participant in allCurrent.Where(item =>
                     !string.IsNullOrWhiteSpace(item.UserName) &&
                     !InstagramOwnAccountFilter.IsOwnParticipant(item, ownNames)))
            InstagramParticipants.Add(participant);
        SelectedInstagramParticipant = InstagramParticipants.FirstOrDefault(item =>
            item.ParticipantId == previouslySelected) ?? InstagramParticipants.FirstOrDefault();
    }

    private async Task LoadSubscriptionRowsAsync()
    {
        var currentChat = SelectedChat;
        var request = ++_subscriptionRowsLoadVersion;
        _subscriptionTileLoadVersion++;
        if (_settings is null || _reader is null || currentChat is null)
        {
            LatestSubscriptionBatch = null;
            IsSubscriptionReviewRequired = false;
            SetLatestParticipantListSource(null);
            _latestParticipantListAttempt = null;
            SubscriptionRows.Clear();
            _subscriptionRowsView.Refresh();
            SubscriptionsSummary = "Выберите Instagram-чат для просмотра участниц.";
            SubscriptionsSyncMessage = "Выберите чат для просмотра списка участниц.";
            SetSubscriptionTileCounts(null);
            return;
        }

        try
        {
            // Read everything before changing the visible rows or last successful batch.
            // A transient database error must not leave the screen empty.
            var settings = await _settings.LoadAsync();
            new InstagramCheckProfileSemanticsMigration(_reader.DatabasePath).EnsureApplied();
            if (!_instagramBusy && !string.IsNullOrWhiteSpace(settings.InstagramTargetUserName))
                new InstagramBatchRunStore(_reader.DatabasePath)
                    .DiscardAbandonedDrafts(settings.InstagramTargetUserName);
            var latestBatch = new InstagramBatchSummaryStore(_reader.DatabasePath)
                .Read(settings.InstagramTargetUserName ?? string.Empty);
            var registry = new ChatParticipantRegistry(_reader.DatabasePath);
            var source = await registry.ReadLatestSourceAsync(currentChat.Chat);
            var latestAttempt = await registry.ReadLastAttemptAsync(currentChat.Chat);
            var lookup = new InstagramParticipantLookup(_reader.DatabasePath);
            var participants = await lookup.ReadAllCurrentAsync([currentChat.Chat]);
            var owned = await new InstagramOwnAccountStore(_reader.DatabasePath)
                .EnsureFromLegacyAsync(settings.InstagramTargetUserName);
            var ownNames = InstagramOwnAccountFilter.GetUserNames(owned, settings.InstagramBrowserUserName);
            // An account marked VIP/admin in any connected chat is exempt from
            // the "subscribed to me" direction in every chat view.
            var privilegedNames = (await lookup.ReadAllCurrentAsync(settings.Chats))
                .Where(participant => InstagramBatchEligibility.IsExcluded(participant.Category) &&
                                      !string.IsNullOrWhiteSpace(participant.UserName))
                .Select(participant => participant.UserName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var userNames = participants.SelectMany(participant => new[]
                {
                    participant.UserName,
                    InstagramActivityProfile.TryGetUserName(participant.SubscriptionProfileUrl)
                })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            IReadOnlyList<InstagramSavedCheck> saved = string.IsNullOrWhiteSpace(settings.InstagramTargetUserName)
                ? []
                : new InstagramSubscriptionCheckStore(_reader.DatabasePath)
                    .Read(settings.InstagramTargetUserName, userNames);
            var rows = InstagramSubscriptionPresentation.Build(participants, saved,
                ownNames, privilegedNames).ToArray();
            var reviewRequired = await new InstagramSubscriptionReviewStateStore(_reader.DatabasePath)
                .NeedsReviewAsync(settings.InstagramTargetUserName, currentChat.Chat,
                    settings.Chats, ownNames);

            // Ignore stale loads when the user switches chats or another reload starts.
            if (request != _subscriptionRowsLoadVersion || !ReferenceEquals(currentChat, SelectedChat))
                return;

            SubscriptionRows.Clear();
            foreach (var row in rows)
                SubscriptionRows.Add(row);
            _subscriptionRowsView.Refresh();
            LatestSubscriptionBatch = latestBatch;
            IsSubscriptionReviewRequired = reviewRequired;
            SetLatestParticipantListSource(source);
            _latestParticipantListAttempt = latestAttempt;
            // The headline counts everyone in the source register; the rows/checks
            // intentionally omit the owner's Instagram identities.
            var registerCounts = InstagramParticipantCategoryCounts.FromRegister(participants);
            SetSubscriptionTileCounts(registerCounts);
            var excludedOwn = participants.Count - SubscriptionRows.Count;
            var discrepancies = SubscriptionRows.Count(row => row.HasDiscrepancy);
            var ownAccountNote = excludedOwn > 0
                ? $" Собственных аккаунтов исключено из проверки: {excludedOwn}."
                : string.Empty;
            SubscriptionsSummary = $"В списке: {registerCounts.Total}. Для проверки: {SubscriptionRows.Count}." +
                                   ownAccountNote + " Расхождений по результатам поиска: " +
                                   $"{discrepancies}. Для VIP и администрации проверяем только мои подписки.";
            if (SubscriptionsSyncMessage.StartsWith("Не удалось загрузить список участниц:",
                    StringComparison.Ordinal) ||
                SubscriptionsSyncMessage == "Выберите чат для просмотра списка участниц.")
                SubscriptionsSyncMessage = string.Empty;
        }
        catch (Exception exception)
        {
            if (request == _subscriptionRowsLoadVersion && ReferenceEquals(currentChat, SelectedChat))
                SubscriptionsSyncMessage = $"Не удалось загрузить список участниц: {exception.Message}";
            throw;
        }
    }

    private bool FilterSubscriptionRow(object value)
    {
        if (value is not InstagramSubscriptionRow row) return false;
        if (ShowOnlySubscriptionDiscrepancies && !row.HasDiscrepancy)
            return false;
        var query = _subscriptionQuery.Trim();
        return query.Length == 0 || row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.ActivityUserName.Contains(query.TrimStart('@'), StringComparison.OrdinalIgnoreCase) ||
               row.SubscriptionUserName.Contains(query.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
    }

    private void OpenParticipantProfileLink_OnRequestNavigate(object sender, RequestNavigateEventArgs args)
    {
        args.Handled = true;
        if (args.Uri is not { IsAbsoluteUri: true } uri) return;
        var profileUrl = uri.AbsoluteUri;
        if (InstagramActivityProfile.TryGetUserName(profileUrl) is null) return;
        try { Process.Start(new ProcessStartInfo(profileUrl) { UseShellExecute = true }); }
        catch (Exception)
        {
            MessageBox.Show("Не удалось открыть Instagram-профиль.", "ChatPulse",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CheckAllInstagramParticipantsButton_OnClick(object sender, RoutedEventArgs args)
        => ShowSubscriptionCheckDialog(null, InstagramCheckDirections.BOTH);

    private void CheckRowIFollowButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: InstagramSubscriptionRow participant } && participant.CanCheckFollowing)
            ShowSubscriptionCheckDialog(participant, InstagramCheckDirections.FOLLOWING);
    }

    private void CheckRowFollowsMeButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: InstagramSubscriptionRow participant } ||
            !participant.CanCheckFollowers) return;
        ShowSubscriptionCheckDialog(participant, InstagramCheckDirections.FOLLOWERS);
    }

    private void ShowSubscriptionCheckDialog(InstagramSubscriptionRow? individual,
        InstagramCheckDirections direction)
    {
        if (_settings is null || _reader is null || _instagramBusy) return;
        if (!InstagramCheckDirectionSelection.IsValid(direction)) return;
        SetInstagramBusy(true);
        using var cancellation = new CancellationTokenSource();
        _instagramBatchCancellation = cancellation;
        Notify(nameof(CanCancelInstagramBatch));
        var dialog = new InstagramBatchProgressDialog(cancellation,
            chooseDirections: individual is null, predetermined: direction,
            individualUserName: individual is null ? null :
                direction == InstagramCheckDirections.FOLLOWERS
                    ? individual.ActivityUserName : individual.SubscriptionUserName);
        var owner = Window.GetWindow(this);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.StartRequested += async (_, _) =>
        {
            try { await RunInstagramBatchAsync(dialog, cancellation.Token, individual); }
            catch (Exception exception)
            {
                dialog.Model.Finish($"Не удалось завершить проверку: {exception.Message}", false);
                _instagramBatchCancellation = null;
                Notify(nameof(CanCancelInstagramBatch));
                SetInstagramBusy(false);
            }
        };
        var mainWindow = owner as ChatPulse.Desktop.MainWindow;
        mainWindow?.ShowDialogOverlay();
        try { dialog.ShowDialog(); }
        finally
        {
            mainWindow?.HideDialogOverlay();
            // A user can cancel direction selection without starting Selenium.
            _instagramBatchCancellation = null;
            Notify(nameof(CanCancelInstagramBatch));
            SetInstagramBusy(false);
        }
    }

    private async Task RunInstagramBatchAsync(InstagramBatchProgressDialog dialog,
        CancellationToken cancellationToken, InstagramSubscriptionRow? individual = null)
    {
        var model = dialog.Model;
        var directions = dialog.SelectedDirections;
        var completed = 0;
        var found = 0;
        var notFound = 0;
        var inconclusive = 0;
        var notFoundFollowers = 0;
        var notFoundFollowing = 0;
        var total = 0;
        var privileged = 0;
        var missingActivity = 0;
        var success = false;
        IReadOnlyList<InstagramReviewFingerprint> reviewedFingerprints = [];
        var runOutcome = InstagramBatchOutcome.FAILED;
        var failureReason = string.Empty;
        var runId = Guid.NewGuid().ToString("N");
        string? targetUserName = null;
        string summary;
        try
        {
            if (!InstagramCheckDirectionSelection.IsValid(directions))
                throw new ArgumentException("Выберите хотя бы одно направление проверки.");
            model.Activate(InstagramBatchStage.SETTINGS,
                "Читаем настройки основного и проверочного Instagram-аккаунтов...");
            var settings = await _settings!.LoadAsync();
            targetUserName = settings.InstagramTargetUserName;
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(settings.InstagramTargetUserName) ||
                string.IsNullOrWhiteSpace(settings.InstagramBrowserUserName))
            {
                summary = "Сначала сохраните основной и проверочный Instagram-аккаунты в настройках.";
                SubscriptionsCheckMessage = summary;
                model.InterruptActive();
            }
            else
            {
                model.Activate(InstagramBatchStage.PARTICIPANTS,
                    "Получаем актуальный список участниц и исключаем собственные аккаунты...");
                new InstagramCheckProfileSemanticsMigration(_reader!.DatabasePath).EnsureApplied();
                var lookup = new InstagramParticipantLookup(_reader.DatabasePath);
                var allCurrent = await lookup.ReadAllCurrentAsync(settings.Chats, cancellationToken);
                var owned = await new InstagramOwnAccountStore(_reader.DatabasePath)
                    .EnsureFromLegacyAsync(settings.InstagramTargetUserName, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var ownNames = InstagramOwnAccountFilter.GetUserNames(owned, settings.InstagramBrowserUserName);
                // Capture the roster actually being checked, before Selenium starts.
                // If Telegram changes it during the run, the baseline will remain stale.
                if (individual is null)
                    reviewedFingerprints = await new InstagramSubscriptionReviewStateStore(_reader.DatabasePath)
                        .CaptureAsync(settings.Chats, ownNames, cancellationToken);
                var plan = InstagramBatchEligibility.Prepare(allCurrent, ownNames);
                privileged = plan.ExcludedCount;
                missingActivity = plan.MissingActivityProfileCount;
                var missingSubscription = plan.MissingSubscriptionProfileCount;
                IReadOnlyList<string> followers;
                IReadOnlyList<string> following;
                if (individual is null)
                {
                    followers = InstagramCheckDirectionSelection.Filter(plan.FollowsMeUserNames,
                        directions, InstagramCheckDirections.FOLLOWERS);
                    following = InstagramCheckDirectionSelection.Filter(plan.IFollowUserNames,
                        directions, InstagramCheckDirections.FOLLOWING);
                }
                else
                {
                    // The participant must still be in the current register and must not
                    // have changed the activity profile since the row was displayed.
                    var current = allCurrent.FirstOrDefault(item =>
                        item.ParticipantId == individual.ParticipantId &&
                        string.Equals(item.UserName, individual.ActivityUserName,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.ActivityProfileUrl, individual.ActivityProfileUrl,
                            StringComparison.OrdinalIgnoreCase));
                    if (current is null || InstagramOwnAccountFilter.IsOwnParticipant(current, ownNames))
                        throw new InvalidOperationException(
                            "Участница больше не присутствует в актуальном списке. Обновите вкладку.");
                    var subscriptionName = InstagramActivityProfile.TryGetUserName(current.SubscriptionProfileUrl);
                    if (!string.Equals(subscriptionName, individual.SubscriptionUserName,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Страница для подписки изменилась. Обновите вкладку.");
                    if (directions == InstagramCheckDirections.FOLLOWERS &&
                        !plan.FollowsMeUserNames.Contains(current.UserName, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Для VIP и администрации подписку на меня не проверяем.");
                    if (directions == InstagramCheckDirections.FOLLOWING &&
                        string.IsNullOrWhiteSpace(subscriptionName))
                        throw new InvalidOperationException("Не найдена корректная страница для подписки.");
                    followers = directions == InstagramCheckDirections.FOLLOWERS ? [current.UserName] : [];
                    following = directions == InstagramCheckDirections.FOLLOWING ? [subscriptionName!] : [];
                    privileged = 0;
                    missingActivity = 0;
                }

                total = followers.Count + following.Count;
                model.ConfigureWorkload(total);
                if (followers.Count == 0) model.Skip(InstagramBatchStage.FOLLOWERS);
                if (following.Count == 0) model.Skip(InstagramBatchStage.FOLLOWING);
                if (individual is null &&
                    (((directions & InstagramCheckDirections.FOLLOWERS) != 0 && missingActivity > 0) ||
                     ((directions & InstagramCheckDirections.FOLLOWING) != 0 && missingSubscription > 0)))
                {
                    summary = "В списке отсутствуют корректные Instagram-профили, необходимые для " +
                              "выбранных направлений. Проверьте распознавание списка.";
                    failureReason = summary;
                    model.InterruptActive();
                }
                else if (total == 0)
                {
                    summary = "Нет актуальных участниц с корректной ссылкой аккаунта активности.";
                    model.InterruptActive();
                }
                else
                {
                    model.Activate(InstagramBatchStage.BROWSER,
                        $"Поисков запланировано: {total}. Запускаем Chrome...");
                    var runStore = new InstagramBatchRunStore(_reader.DatabasePath);
                    runStore.DiscardAbandonedDrafts(settings.InstagramTargetUserName);
                    var result = await _instagramBrowser.CheckParticipantsAsync(
                        settings.InstagramTargetUserName, followers,
                        following, progress =>
                        {
                            // Stage only until success. Interrupted runs are discarded; last successful
                            // results remain untouched.
                            runStore.Stage(runId, settings.InstagramTargetUserName,
                                settings.InstagramBrowserUserName, progress);
                            completed = progress.Completed;
                            switch (progress.Result.Status)
                            {
                                case InstagramSearchStatus.FOUND:
                                    found++;
                                    break;
                                case InstagramSearchStatus.NOT_FOUND:
                                    notFound++;
                                    if (progress.Kind == "followers") notFoundFollowers++;
                                    else if (progress.Kind == "following") notFoundFollowing++;
                                    break;
                                default: inconclusive++; break;
                            }

                            var foundSnapshot = found;
                            var notFoundSnapshot = notFound;
                            var inconclusiveSnapshot = inconclusive;
                            Dispatcher.BeginInvoke(new Action(() =>
                                model.Report(progress, foundSnapshot, notFoundSnapshot, inconclusiveSnapshot)));
                        }, cancellationToken, stage =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                switch (stage)
                                {
                                    case InstagramBatchBrowserStage.STARTING_CHROME:
                                        model.Activate(InstagramBatchStage.BROWSER,
                                            "Запускаем Chrome с сохранённым профилем Instagram...");
                                        break;
                                    case InstagramBatchBrowserStage.CHECKING_SESSION:
                                        model.Activate(InstagramBatchStage.BROWSER,
                                            "Проверяем сохранённую Instagram-сессию...");
                                        break;
                                    case InstagramBatchBrowserStage.CHECKING_FOLLOWERS:
                                        model.Activate(InstagramBatchStage.FOLLOWERS,
                                            "Проверяем подписки обычных участниц на меня...");
                                        break;
                                    case InstagramBatchBrowserStage.CHECKING_FOLLOWING:
                                        model.Activate(InstagramBatchStage.FOLLOWING,
                                            "Проверяем мои подписки на всех участниц, включая VIP и администрацию...");
                                        break;
                                }
                            }));
                        });
                    // A search with INCONCLUSIVE is not a successful check. Never
                    // replace the previous published snapshot with partial/failed results.
                    success = result.StopReason is null && completed == total && inconclusive == 0;
                    runOutcome = success ? InstagramBatchOutcome.COMPLETED :
                        cancellationToken.IsCancellationRequested ? InstagramBatchOutcome.STOPPED :
                        InstagramBatchOutcome.FAILED;
                    if (!success)
                        failureReason = result.StopReason ?? (inconclusive > 0
                            ? $"Не удалось выполнить {inconclusive} поисковых проверок."
                            : "Не все поиски выполнены.");
                    summary = success
                        ? $"Проверка завершена: {completed} из {total} поисков. " +
                          $"Найдено: {found}; не найдено: {notFound}; с ошибкой: {inconclusive}."
                        : $"Проверка не сохранена: {failureReason} " +
                          $"Выполнено {completed} из {total} поисков. Предыдущие успешные результаты не изменены.";
                    if (!success) model.InterruptActive();
                }

                SubscriptionsCheckMessage = summary;
            }
        }
        catch (OperationCanceledException)
        {
            runOutcome = InstagramBatchOutcome.STOPPED;
            failureReason = "Остановлено пользователем.";
            summary = $"Остановлено пользователем после {completed} из {total} поисков. " +
                      "Временные результаты будут удалены.";
            SubscriptionsCheckMessage = summary;
            model.InterruptActive();
        }
        catch (Exception exception)
        {
            runOutcome = InstagramBatchOutcome.FAILED;
            failureReason = exception.Message;
            summary = $"Ошибка проверки после {completed} поисков: {exception.Message}. " +
                      "Предыдущая успешная проверка остаётся без изменений.";
            SubscriptionsCheckMessage = summary;
            model.InterruptActive();
        }
        finally
        {
            model.Activate(InstagramBatchStage.FINISHING,
                "Закрываем Chrome и завершаем обработку результатов...");
            try { await _instagramBrowser.CloseBrowserAsync(); }
            catch (Exception exception)
            {
                success = false;
                runOutcome = InstagramBatchOutcome.FAILED;
                failureReason += $" Не удалось закрыть Chrome: {exception.Message}";
                SubscriptionsCheckMessage += $" Не удалось закрыть Chrome: {exception.Message}";
            }

            // Stopping right after the final Selenium result must still be respected.
            if (success && cancellationToken.IsCancellationRequested)
            {
                success = false;
                runOutcome = InstagramBatchOutcome.STOPPED;
                failureReason = "Остановлено пользователем до публикации результатов.";
                model.InterruptActive();
            }

            // All completed searches are already saved as drafts. Publish their results
            // and the summary in ONE transaction only after a successful full run.
            if (total > 0 && !string.IsNullOrWhiteSpace(targetUserName))
            {
                var runStore = new InstagramBatchRunStore(_reader!.DatabasePath);
                var snapshot = new InstagramBatchResultSummary(
                    DateTimeOffset.UtcNow, runOutcome, completed, total, found, notFound,
                    inconclusive, privileged, missingActivity,
                    success ? string.Empty : failureReason.Trim(),
                    notFoundFollowers, notFoundFollowing, directions);
                if (success)
                {
                    try
                    {
                        if (individual is null)
                        {
                            runStore.PublishCompleted(runId, targetUserName, snapshot,
                                reviewedFingerprints);
                            LatestSubscriptionBatch = snapshot;
                        }
                        else
                        {
                            runStore.PublishIndividual(runId, targetUserName,
                                directions == InstagramCheckDirections.FOLLOWERS
                                    ? individual.ActivityUserName
                                    : individual.SubscriptionUserName,
                                directions);
                        }

                        SubscriptionsCheckMessage = string.Empty;
                    }
                    catch (Exception exception)
                    {
                        success = false;
                        runOutcome = InstagramBatchOutcome.FAILED;
                        failureReason = $"Не удалось опубликовать результаты проверки: {exception.Message}";
                        SubscriptionsCheckMessage = failureReason +
                                                    " Предыдущая успешная проверка сохранена.";
                        snapshot = snapshot with { Outcome = InstagramBatchOutcome.FAILED, Details = failureReason };
                        model.InterruptActive();
                    }
                }

                if (!success)
                {
                    // Discard temporary draft records; keep only published successful results.
                    snapshot = snapshot with
                    {
                        Outcome = runOutcome,
                        Details = string.IsNullOrWhiteSpace(failureReason)
                            ? "Проверка прервана. Результаты последней успешной проверки сохранены."
                            : failureReason
                    };
                    try { runStore.RecordInterrupted(runId, targetUserName, snapshot); }
                    catch (Exception exception)
                    {
                        SubscriptionsCheckMessage += $" Не удалось очистить временные результаты: {exception.Message}";
                    }

                    if (string.IsNullOrWhiteSpace(SubscriptionsCheckMessage))
                        SubscriptionsCheckMessage = individual is null
                            ? "Проверка прервана. Показаны результаты предыдущей успешной проверки."
                            : "Выборочная проверка прервана. Прежний статус участницы сохранён.";
                }

                model.SetResultSummary(snapshot);
            }

            // Refresh only after publishing. On interruption the existing view and its
            // last completed result remain unchanged.
            if (success && _page == WorkspacePage.Chat && _chatPage == ChatPage.Subscriptions)
            {
                try { await LoadSubscriptionRowsAsync(); }
                catch (Exception exception)
                {
                    SubscriptionsCheckMessage =
                        $"Проверка сохранена, но не удалось обновить таблицу: {exception.Message}";
                }
            }

            _instagramBatchCancellation = null;
            Notify(nameof(CanCancelInstagramBatch));
            SetInstagramBusy(false);
            model.Finish(SubscriptionsCheckMessage, success);
        }
    }

    private async void CheckInstagramParticipantButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_settings is null || _instagramBusy || _reader is null) return;
        SetInstagramBusy(true);
        var runId = Guid.NewGuid().ToString("N");
        var target = string.Empty;
        var stageStarted = false;
        var runStore = new InstagramBatchRunStore(_reader.DatabasePath);
        try
        {
            await LoadInstagramParticipantsAsync();
            await LoadLastParticipantHistoryReportAsync();
            var participant = SelectedInstagramParticipant;
            if (participant is null)
            {
                InstagramStatus = "В актуальном списке нет участниц для проверки.";
                return;
            }

            var saved = await _settings.LoadAsync();
            target = saved.InstagramTargetUserName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target) ||
                string.IsNullOrWhiteSpace(saved.InstagramBrowserUserName))
            {
                InstagramStatus = "Сначала сохраните основной и проверочный Instagram-аккаунты.";
                return;
            }

            new InstagramCheckProfileSemanticsMigration(_reader.DatabasePath).EnsureApplied();
            runStore.DiscardAbandonedDrafts(target);
            var subscriptionUserName = InstagramActivityProfile.TryGetUserName(
                participant.SubscriptionProfileUrl);
            if (string.IsNullOrWhiteSpace(subscriptionUserName))
                throw new InvalidOperationException("Не указана корректная страница для подписки.");
            var privileged = InstagramParticipants.Any(item =>
                string.Equals(item.UserName, participant.UserName, StringComparison.OrdinalIgnoreCase) &&
                InstagramBatchEligibility.IsExcluded(item.Category));
            var directions = privileged ? InstagramCheckDirections.FOLLOWING : InstagramCheckDirections.BOTH;
            string[] followers = privileged ? [] : [participant.UserName];
            var following = new[] { subscriptionUserName };
            InstagramStatus = $"Проверяем подписку на @{subscriptionUserName} и " +
                              (privileged
                                  ? "пропускаем обратное направление."
                                  : $"подписку @{participant.UserName} на меня.") +
                              " Не закрывайте Chrome.";
            stageStarted = true;
            var run = await _instagramBrowser.CheckParticipantsAsync(target, followers, following,
                progress => runStore.Stage(runId, target, saved.InstagramBrowserUserName, progress));
            if (run.StopReason is not null || run.Completed != run.Total)
                throw new InvalidOperationException(run.StopReason ?? "Не все поиски завершены.");
            // Both directions become visible together, not one at a time.
            runStore.PublishIndividualDirections(runId, target, participant.UserName,
                subscriptionUserName, directions);
            var latest = new InstagramSubscriptionCheckStore(_reader.DatabasePath)
                .Read(target, new[] { participant.UserName, subscriptionUserName });
            var followersResult = latest.FirstOrDefault(item => item.Kind == "followers" &&
                                                                string.Equals(item.UserName, participant.UserName,
                                                                    StringComparison.OrdinalIgnoreCase));
            var followingResult = latest.FirstOrDefault(item => item.Kind == "following" &&
                                                                string.Equals(item.UserName, subscriptionUserName,
                                                                    StringComparison.OrdinalIgnoreCase));

            static string DescribeSaved(InstagramSavedCheck? savedCheck) => savedCheck?.Status switch
            {
                "FOUND" => "Найдена",
                "NOT_FOUND" => "Не найдена в поиске",
                "INCONCLUSIVE" => "Не удалось проверить",
                _ => "Не проверено"
            };

            InstagramStatus = $"Проверка @{participant.UserName}\n" +
                              $"Участница → я (@{participant.UserName}): " +
                              (privileged ? "—" : DescribeSaved(followersResult)) + "\n" +
                              $"Я → участница (@{subscriptionUserName}): " +
                              DescribeSaved(followingResult);
            if (_page == WorkspacePage.Chat && _chatPage == ChatPage.Subscriptions)
                await LoadSubscriptionRowsAsync();
        }
        catch (Exception exception)
        {
            if (stageStarted)
            {
                try
                {
                    runStore.RecordInterrupted(runId, target, new InstagramBatchResultSummary(
                        DateTimeOffset.UtcNow, InstagramBatchOutcome.FAILED, 0, 0, 0, 0, 0, 0, 0));
                }
                catch (Exception cleanupError)
                {
                    InstagramStatus = $"Не удалось очистить временные результаты: {cleanupError.Message}";
                }
            }

            InstagramStatus = $"Проверка не завершена: {exception.Message}. " +
                              "Результаты последней успешной проверки сохранены.";
        }
        finally
        {
            try { await _instagramBrowser.CloseBrowserAsync(); }
            catch (Exception exception)
            {
                InstagramStatus += $" Не удалось закрыть Chrome: {exception.Message}";
            }

            SetInstagramBusy(false);
        }
    }

    private async void SaveInstagramButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_settings is null || _reader is null || _instagramBusy) return;
        SetInstagramBusy(true);
        try
        {
            var main = InstagramUserName.Normalize(InstagramTargetBox.Text);
            var reserve = InstagramUserName.Normalize(InstagramGiveFromBox.Text);
            if (string.Equals(main, reserve, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Основной и резервный аккаунты должны отличаться.");

            var previous = await _settings.LoadAsync();
            var previousOwn = await new InstagramOwnAccountStore(_reader.DatabasePath)
                .EnsureFromLegacyAsync(previous.InstagramTargetUserName);
            // Never silently reuse an authenticated profile under a different declared identity.
            if (previous.InstagramBrowserUserName is { } browserName &&
                !string.Equals(browserName, reserve, StringComparison.OrdinalIgnoreCase) &&
                !_instagramSessionCleared)
            {
                InstagramStatus = "Текущий Chrome-профиль принадлежит @" + browserName +
                                  ". Для назначения другого резервного аккаунта сначала сбросьте резервную сессию.";
                return;
            }
            if (previousOwn is not null &&
                !string.Equals(previousOwn.ReceivingUserName, main, StringComparison.OrdinalIgnoreCase) &&
                !_mainInstagramSessionCleared)
            {
                InstagramStatus = "Для смены основного аккаунта сначала сбросьте его Chrome-сессию " +
                                  "и подтвердите новую авторизацию.";
                return;
            }
            await _settings.SaveInstagramAccountsAsync(main, reserve);
            await new InstagramOwnAccountStore(_reader.DatabasePath).SaveAsync(main, reserve);
            await LoadInstagramParticipantsAsync();
            _instagramSessionCleared = false;
            _mainInstagramSessionCleared = false;
            InstagramStatus = "Аккаунты сохранены. Основная и резервная сессии Chrome независимы.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            InstagramStatus = exception.Message;
        }
        finally { SetInstagramBusy(false); }
    }

    private async Task<string?> GetConfiguredInstagramAccountAsync(bool isMain)
    {
        if (_settings is null || _reader is null) return null;
        var state = await _settings.LoadAsync();
        var own = await new InstagramOwnAccountStore(_reader.DatabasePath)
            .EnsureFromLegacyAsync(state.InstagramTargetUserName);
        return isMain ? own?.ReceivingUserName : own?.GivingUserName;
    }

    private async Task RunInstagramSessionActionAsync(bool isMain, string operation)
    {
        if (_instagramBusy)
        {
            InstagramStatus = "Дождитесь завершения текущей операции Instagram.";
            return;
        }
        SetInstagramBusy(true);
        var profileName = isMain ? "основного" : "резервного";
        InstagramStatus = operation switch
        {
            "open" => $"Открываем Chrome для {profileName} аккаунта…",
            "check" => $"Проверяем сессию {profileName} аккаунта…",
            "reset" => $"Подготавливаем сброс сессии {profileName} аккаунта…",
            _ => $"Выполняем операцию с {profileName} аккаунтом…"
        };
        try
        {
            var account = await GetConfiguredInstagramAccountAsync(isMain);
            if (string.IsNullOrWhiteSpace(account))
            {
                InstagramStatus = $"Не настроен {profileName} Instagram-аккаунт. " +
                                  "Укажите оба username и нажмите «Сохранить аккаунты».";
                return;
            }
            var browser = isMain ? _mainInstagramBrowser : _instagramBrowser;
            if (operation == "open")
            {
                await browser.OpenLoginAsync();
                InstagramStatus = $"Chrome для @{account} открыт. Выполните вход вручную.";
            }
            else if (operation == "check")
            {
                var confirmed = await browser.IsLoggedInAsAsync(account);
                InstagramStatus = confirmed
                    ? $"Chrome-сессия @{account} подтверждена."
                    : $"Не удалось подтвердить Chrome-сессию @{account}. " +
                      "Проверьте, какой аккаунт открыт в соответствующем Chrome.";
            }
            else if (operation == "reset")
            {
                if (MessageBox.Show($"Удалить только Chrome-сессию @{account}? " +
                        "Для повторного входа потребуется авторизация Instagram.",
                        "ChatPulse — Instagram", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                await browser.ClearSessionAsync();
                if (isMain) _mainInstagramSessionCleared = true;
                else _instagramSessionCleared = true;
                InstagramStatus = $"Локальная Chrome-сессия @{account} очищена.";
            }
        }
        catch (Exception exception)
        {
            InstagramStatus = $"Операция с Chrome не завершена: {exception.Message}";
        }
        finally { SetInstagramBusy(false); }
    }

    private async void OpenMainInstagram_OnClick(object sender, RoutedEventArgs args)
        => await RunInstagramSessionActionAsync(true, "open");

    private async void CheckMainInstagram_OnClick(object sender, RoutedEventArgs args)
        => await RunInstagramSessionActionAsync(true, "check");

    private async void ResetMainInstagram_OnClick(object sender, RoutedEventArgs args)
        => await RunInstagramSessionActionAsync(true, "reset");

    private async void InstagramLoginButton_OnClick(object sender, RoutedEventArgs args)
        => await RunInstagramSessionActionAsync(false, "open");

    private async void CheckInstagramButton_OnClick(object sender, RoutedEventArgs args)
        => await RunInstagramSessionActionAsync(false, "check");

    private async void ResetInstagramButton_OnClick(object sender, RoutedEventArgs args)
        => await RunInstagramSessionActionAsync(false, "reset");

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
                                  ? string.Empty
                                  : $"\n{followers.Diagnostics}") +
                              "\nЭто диагностический результат: проверка подписок пока не выполняется.";
        }
        catch (Exception exception)
        {
            InstagramStatus = $"Не удалось получить списки Instagram: {exception.Message}";
        }
        finally { SetInstagramBusy(false); }
    }

    public bool IsHomeVisible => _page == WorkspacePage.Home;
    public bool IsChatVisible => _page == WorkspacePage.Chat;
    public bool IsSettingsVisible => _page == WorkspacePage.Settings;
    public bool IsTasksVisible => _chatPage == ChatPage.Tasks;
    public bool IsSubscriptionsVisible => _chatPage == ChatPage.Subscriptions;
    public bool IsMyActivityVisible => _chatPage == ChatPage.MyActivity;
    public bool IsSidebarExpanded => _isSidebarExpanded;
    public bool IsSubscriptionTableExpanded => _isSubscriptionTableExpanded;
    public string SubscriptionTableExpandLabel => _isSubscriptionTableExpanded ? "Свернуть" : "Развернуть";
    public bool IsPendingTasksSelected => _page == WorkspacePage.Chat &&
        _chatPage == ChatPage.Tasks && _taskFilter == TaskFilter.Pending && SelectedChat is not null;
    public bool IsCompletedTasksSelected => _page == WorkspacePage.Chat &&
        _chatPage == ChatPage.Tasks && _taskFilter == TaskFilter.Completed && SelectedChat is not null;
    public bool IsTaskListExpanded => _isTaskListExpanded;
    public string TaskListExpandLabel => _isTaskListExpanded ? "Свернуть задания" : "Развернуть задания";
    public bool IsChatContentExpanded => _isTaskListExpanded || _isSubscriptionTableExpanded;

    private void SetSubscriptionTableExpanded(bool expanded)
    {
        if (_isSubscriptionTableExpanded == expanded) return;
        _isSubscriptionTableExpanded = expanded;
        Notify(nameof(IsSubscriptionTableExpanded));
        Notify(nameof(SubscriptionTableExpandLabel));
        Notify(nameof(IsChatContentExpanded));
    }

    private void SetTaskListExpanded(bool expanded)
    {
        if (_isTaskListExpanded == expanded) return;
        _isTaskListExpanded = expanded;
        Notify(nameof(IsTaskListExpanded));
        Notify(nameof(TaskListExpandLabel));
        Notify(nameof(IsChatContentExpanded));
    }

    private void ToggleSubscriptionTableExpansionButton_OnClick(object sender, RoutedEventArgs args)
        => SetSubscriptionTableExpanded(!_isSubscriptionTableExpanded);

    private void ToggleTaskListExpansionButton_OnClick(object sender, RoutedEventArgs args)
        => SetTaskListExpanded(!_isTaskListExpanded);

    private void RestoreSubscriptionColumnWidths(AppSettings settings)
    {
        if (_subscriptionColumnsRestored) return;
        _restoringSubscriptionColumnWidths = true;
        try
        {
            var widths = settings.SubscriptionColumnWidths;
            for (var index = 0; index < SubscriptionDataGrid.Columns.Count &&
                                index < SubscriptionColumnKeys.Length; index++)
            {
                var column = SubscriptionDataGrid.Columns[index];
                if (widths is not null && widths.TryGetValue(SubscriptionColumnKeys[index], out var width) &&
                    double.IsFinite(width) && width >= column.MinWidth && width <= 4000)
                    column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
            }
            _subscriptionColumnsRestored = true;
        }
        finally
        {
            _restoringSubscriptionColumnWidths = false;
        }
    }

    private void SubscriptionColumnWidth_OnChanged(object? sender, EventArgs args)
    {
        if (!_subscriptionColumnsRestored || _restoringSubscriptionColumnWidths || _settings is null)
            return;
        _subscriptionColumnSaveTimer.Stop();
        _subscriptionColumnSaveTimer.Start();
    }

    private async void SubscriptionColumnSaveTimer_OnTick(object? sender, EventArgs args)
    {
        _subscriptionColumnSaveTimer.Stop();
        if (!_subscriptionColumnsRestored || _settings is null) return;
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var index = 0; index < SubscriptionDataGrid.Columns.Count &&
                            index < SubscriptionColumnKeys.Length; index++)
        {
            var column = SubscriptionDataGrid.Columns[index];
            var width = column.ActualWidth;
            if (double.IsFinite(width) && width >= column.MinWidth && width <= 4000)
                widths[SubscriptionColumnKeys[index]] = Math.Round(width, 1);
        }
        if (widths.Count != SubscriptionColumnKeys.Length) return;
        try { await _settings.SaveSubscriptionColumnWidthsAsync(widths); }
        catch (Exception)
        {
            // Settings changes must not interrupt subscription checks or table interaction.
        }
    }

    public void Initialize(ISettingsService settings, WorkspaceTaskReader reader,
        ChatSynchronizationService synchronization,
        ITelegramParticipantListHistoryReader participantHistoryReader,
        ITelegramAuthenticationClient telegram)
    {
        _settings = settings;
        _reader = reader;
        _synchronization = synchronization;
        _instagramChatConnection = new InstagramChatConnectionService(telegram, settings, synchronization);
        _participantHistoryReader = participantHistoryReader;
        Notify(nameof(CanRebuildParticipantHistory));
        _skipStore = new WorkspaceTaskSkipStore(reader.DatabasePath);
        _hideStore = new WorkspaceCompletedTaskHideStore(reader.DatabasePath);
    }

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (IsVisible && _settings is not null && _reader is not null)
            await ReloadAsync();
        else if (!IsVisible)
        {
            _loadVersion++;
            _taskWindowVersion++;
            _taskWindowCancellation?.Cancel();
        }
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
            RestoreSubscriptionColumnWidths(settings);

            _isRebuildingChatList = true;
            try
            {
                var previous = SelectedChat?.Chat;
                var historyPrevious = SelectedHistoryChat?.Chat;
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
                SelectedHistoryChat = Chats.FirstOrDefault(row => historyPrevious is not null &&
                    row.Chat.TelegramUserId == historyPrevious.TelegramUserId &&
                    row.Chat.PeerType == historyPrevious.PeerType &&
                    row.Chat.PeerId == historyPrevious.PeerId) ??
                    Chats.FirstOrDefault(row => ChatParticipantHistoryRuleSelection.SupportsChat14(row.Chat)) ??
                    Chats.FirstOrDefault();
            }
            finally
            {
                _isRebuildingChatList = false;
            }

            Notify(nameof(ConnectedChatCount));
            foreach (var chat in Chats)
            {
                // Scan without materializing all task cards; only requested pages are retained.
                var summary = await _reader!.ReadPageAsync(chat.Chat, WorkspaceTaskState.All,
                    false, 0, 0);
                if (version != _loadVersion) return;
                chat.DetectedCount = summary.NonSkippedTotal;
                _totalDetected += chat.DetectedCount;
                Notify(nameof(TotalDetected));
                Notify(nameof(SelectedChatTaskCount));
            }

            UpdateSelectedTasks();
            await LoadSynchronizationStampsAsync();
            if (_page == WorkspacePage.Chat && _chatPage == ChatPage.Subscriptions)
                await LoadSubscriptionRowsAsync();
            else if (_page == WorkspacePage.Chat)
                await RefreshSubscriptionTileCountsAsync(SelectedChat);
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

    private WorkspaceTaskState CurrentTaskState => _taskFilter switch
    {
        TaskFilter.Completed => WorkspaceTaskState.Completed,
        TaskFilter.Skipped => WorkspaceTaskState.Skipped,
        _ => WorkspaceTaskState.Pending
    };

    private void SetTaskPageLoading(bool value)
    {
        _isTaskPageLoading = value;
        Notify(nameof(IsTaskPageLoading));
        Notify(nameof(HasNoTasks));
    }

    private void SetTaskPageError(string value)
    {
        _taskPageError = value;
        Notify(nameof(TaskPageError));
        Notify(nameof(HasTaskPageError));
    }

    private void UpdateSelectedTasks()
    {
        var version = ++_taskWindowVersion;
        _taskWindowCancellation?.Cancel();
        _taskWindowCancellation?.Dispose();
        _taskWindowCancellation = new CancellationTokenSource();
        _taskPages.Clear();
        _taskTotalCount = 0;
        _taskCache.Clear();
        Tasks.Clear();
        SetTaskPageError(string.Empty);
        Notify(nameof(HasTasks));
        Notify(nameof(HasNoTasks));
        if (SelectedChat is not { } chat || _reader is null)
        {
            TaskSummary = "Подключённых чатов пока нет.";
            SetTaskPageLoading(false);
            return;
        }

        SetTaskPageLoading(true);
        TaskSummary = "Загружаем задания…";
        _ = LoadInitialTaskPageAsync(chat, version);
    }

    private async Task LoadInitialTaskPageAsync(WorkspaceChatRow chat, int version)
    {
        try
        {
            var result = await _reader!.ReadPageAsync(chat.Chat, CurrentTaskState,
                _oldestTasksFirst, 0, TASK_PAGE_SIZE,
                _taskWindowCancellation!.Token);
            if (version != _taskWindowVersion || !ReferenceEquals(SelectedChat, chat)) return;
            _taskTotalCount = result.Total;
            _taskPages[0] = result.Tasks;
            RenderTaskPages(chat);
            TaskListScroll.ScrollToTop();
        }
        catch (OperationCanceledException) when (version != _taskWindowVersion) {/*ignore*/ }
        catch (Exception exception)
        {
            if (version == _taskWindowVersion)
            {
                _failedTaskPage = 0;
                _failedTaskPagePrepend = false;
                SetTaskPageError($"Не удалось загрузить задания: {exception.Message}");
            }
        }
        finally
        {
            if (version == _taskWindowVersion) SetTaskPageLoading(false);
        }
    }

    private void RenderTaskPages(WorkspaceChatRow chat)
    {
        _isRenderingTasks = true;
        try
        {
            var loaded = _taskPages.Values.SelectMany(page => page).ToArray();
            _taskCache[chat] = loaded;
            Tasks.Clear();
            foreach (var task in loaded)
                Tasks.Add(new WorkspaceTaskCard(task));
            TaskSummary = _taskFilter switch
            {
                TaskFilter.Completed => $"Выполненных: {_taskTotalCount}. Статус определяется по твоим реакциям в Telegram.",
                TaskFilter.Skipped => $"Пропущенных: {_taskTotalCount}. Реакции в Telegram не изменяются.",
                _ => $"Невыполненных: {_taskTotalCount}. Статус определяется по твоим реакциям в Telegram."
            };
            Notify(nameof(HasTasks));
            Notify(nameof(HasNoTasks));
            Notify(nameof(EmptyTasksMessage));
        }
        finally { _isRenderingTasks = false; }
        if (_taskFilter == TaskFilter.Completed)
            _ = RefreshFailedVerificationIndicatorsAsync(chat, _taskWindowVersion);
    }

    private async Task RefreshFailedVerificationIndicatorsAsync(WorkspaceChatRow chat, int version)
    {
        if (_reader is null || !ReferenceEquals(SelectedChat, chat)) return;
        var ids = Tasks.Select(item => item.MessageId).ToArray();
        if (ids.Length == 0) return;
        try
        {
            var found = await new WorkspaceFailedTaskVerificationStore(_reader.DatabasePath)
                .FindWithReportsAsync(chat.Chat, ids);
            if (version != _taskWindowVersion || !ReferenceEquals(SelectedChat, chat) ||
                _taskFilter != TaskFilter.Completed) return;
            foreach (var item in Tasks)
                item.HasFailedVerification = found.Contains(item.MessageId);
        }
        catch (Exception exception)
        {
            // Report lookup must not block task paging. The report remains safely in SQLite.
            SetTaskPageError($"Не удалось загрузить отметки отчётов: {exception.Message}");
        }
    }

    private async void TaskListScroll_OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (_isRenderingTasks || _isTaskPageLoading || _reader is null ||
            SelectedChat is null || _taskPages.Count == 0 || !IsTasksVisible) return;
        if (args.ExtentHeightChange != 0 && args.VerticalChange == 0) return;
        if (args.VerticalChange < 0 && args.VerticalOffset < 180 && _taskPages.First().Key > 0)
        {
            await LoadAdjacentTaskPageAsync(true);
        }
        else if (args.VerticalChange > 0 &&
                 args.VerticalOffset >= args.ExtentHeight - args.ViewportHeight - 240 &&
                 (_taskPages.Last().Key + 1) * TASK_PAGE_SIZE < _taskTotalCount)
        {
            await LoadAdjacentTaskPageAsync(false);
        }
    }

    private async Task LoadAdjacentTaskPageAsync(bool prepend)
    {
        if (_isTaskPageLoading || _reader is null || SelectedChat is not { } chat || _taskPages.Count == 0)
            return;
        var version = _taskWindowVersion;
        var page = prepend ? _taskPages.First().Key - 1 : _taskPages.Last().Key + 1;
        if (page < 0 || page * TASK_PAGE_SIZE >= _taskTotalCount) return;
        var priorOffset = TaskListScroll.VerticalOffset;
        var removedTopPageHeight = EstimateFirstPageHeight();
        SetTaskPageLoading(true);
        SetTaskPageError(string.Empty);
        try
        {
            var result = await _reader.ReadPageAsync(chat.Chat, CurrentTaskState,
                _oldestTasksFirst, page * TASK_PAGE_SIZE, TASK_PAGE_SIZE,
                _taskWindowCancellation!.Token, includeTotals: false);
            if (version != _taskWindowVersion || !ReferenceEquals(SelectedChat, chat)) return;
            // Keep the original total from the initial snapshot. Adjacent reads are bounded.
            _taskPages[page] = result.Tasks;
            var removedFromStart = false;
            if (_taskPages.Count > TASK_MAX_CACHED_PAGES)
            {
                var evicted = prepend ? _taskPages.Last().Key : _taskPages.First().Key;
                removedFromStart = !prepend;
                _taskPages.Remove(evicted);
            }
            RenderTaskPages(chat);
            // The visible window is bounded; preserve the user's position approximately
            // across a page prepend or eviction without keeping removed cards alive.
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != _taskWindowVersion) return;
                TaskListScroll.UpdateLayout();
                if (prepend)
                {
                    var addedHeight = EstimateFirstPageHeight();
                    TaskListScroll.ScrollToVerticalOffset(priorOffset + addedHeight);
                }
                else if (removedFromStart)
                {
                    TaskListScroll.ScrollToVerticalOffset(Math.Max(0, priorOffset - removedTopPageHeight));
                }
                else TaskListScroll.ScrollToVerticalOffset(priorOffset);
            }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (version != _taskWindowVersion) {/*ignore*/ }
        catch (Exception exception)
        {
            if (version == _taskWindowVersion)
            {
                _failedTaskPage = page;
                _failedTaskPagePrepend = prepend;
                SetTaskPageError($"Не удалось подгрузить сообщения: {exception.Message}");
            }
        }
        finally
        {
            if (version == _taskWindowVersion) SetTaskPageLoading(false);
        }
    }

    private double EstimateFirstPageHeight()
    {
        var displayedCount = Math.Min(TASK_PAGE_SIZE, Tasks.Count);
        var height = 0d;
        for (var index = 0; index < displayedCount; index++)
        {
            if (TaskItemsControl.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement item)
                height += item.ActualHeight;
        }
        return height;
    }

    private async void RetryTaskPageButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_failedTaskPage is null) return;
        if (_failedTaskPage == 0) UpdateSelectedTasks();
        else await LoadAdjacentTaskPageAsync(_failedTaskPagePrepend);
    }

    private async void ChatList_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isRebuildingChatList || ChatList.SelectedItem is not WorkspaceChatRow row) return;
        SelectedChat = row;
        UpdateSelectedTasks();
        ShowChat();
        await LoadSynchronizationStampsAsync();
    }

    private async void OpenChatButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: WorkspaceChatRow row }) return;
        SelectedChat = row;
        _isRebuildingChatList = true;
        try { ChatList.SelectedItem = row; }
        finally { _isRebuildingChatList = false; }

        UpdateSelectedTasks();
        ShowChat();
        await LoadSynchronizationStampsAsync();
    }

    private void ShowChat()
    {
        if (SelectedChat is null) return;
        SetSubscriptionTableExpanded(false);
        SetTaskListExpanded(false);
        _subscriptionRowsLoadVersion++;
        _chatPage = ChatPage.Tasks;
        SubscriptionRows.Clear();
        LatestSubscriptionBatch = null;
        SetLatestParticipantListSource(null);
        _latestParticipantListAttempt = null;
        SubscriptionsSyncMessage = string.Empty;
        SetSubscriptionTileCounts(null);
        _taskFilter = TaskFilter.Pending;
        UpdateSelectedTasks();
        _page = WorkspacePage.Chat;
        NotifyPageState();
        _ = RefreshSubscriptionTileCountsAsync(SelectedChat);
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

    private async void ParticipantHistoryChat_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox comboBox)
            SelectedHistoryChat = comboBox.SelectedItem as WorkspaceChatRow;
        await LoadLastParticipantHistoryReportAsync();
    }

    private async Task LoadLastParticipantHistoryReportAsync()
    {
        var selected = SelectedHistoryChat;
        if (_reader is null || selected is null)
        {
            ParticipantHistoryLastReport = string.Empty;
            return;
        }
        try
        {
            var last = await new ChatParticipantHistoryStore(_reader.DatabasePath)
                .ReadLastRunAsync(selected.Chat);
            if (!ReferenceEquals(SelectedHistoryChat, selected)) return;
            ParticipantHistoryLastReport = last is null
                ? "Полная история этого чата ещё не восстанавливалась."
                : $"Последнее обновление истории: {last.Value.Outcome}." +
                  (string.IsNullOrWhiteSpace(last.Value.Errors)
                      ? string.Empty : $"\nОшибки списков: {last.Value.Errors}");
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(SelectedHistoryChat, selected))
                ParticipantHistoryLastReport =
                    $"Не удалось прочитать отчёт истории: {exception.Message}";
        }
    }

    private void RebuildParticipantHistoryButton_OnClick(object sender, RoutedEventArgs args)
    {
        var selected = SelectedHistoryChat;
        if (!CanRebuildParticipantHistory || selected is null || _participantHistoryReader is null ||
            _reader is null)
            return;

        var chat = selected.Chat;
        using var cancellation = new CancellationTokenSource();
        _historyImportCancellation = cancellation;
        _historyImportRunning = true;
        SetInstagramBusy(true);
        Notify(nameof(CanRebuildParticipantHistory));

        var dialog = new ParticipantHistoryProgressDialog(selected.Title, cancellation);
        dialog.StartRequested += async (_, _) =>
        {
            try
            {
                var store = new ChatParticipantHistoryStore(_reader.DatabasePath);
                var service = new ChatParticipantHistoryImportService(_participantHistoryReader,
                    store, new Chat14ParticipantHistoryParser());
                void Post(Action action)
                {
                    _ = dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
                }

                var result = await service.RebuildAsync(chat,
                    (url, token) => _instagramBrowser.ProfileOpensAsync(url, token),
                    cancellation.Token,
                    (pages, scanned, found) => Post(() =>
                        dialog.SetPageProgress(pages, scanned, found)),
                    (processed, total) => Post(() =>
                        dialog.SetParsingProgress(processed, total)),
                    () => Post(dialog.SetSaving));

                (string Outcome, string? Errors)? report = null;
                string? reportError = null;
                try { report = await store.ReadLastRunAsync(chat); }
                catch (Exception exception) { reportError = exception.Message; }
                var title = result.Outcome switch
                {
                    "COMPLETED" => "История восстановлена",
                    "PARTIAL" => "История восстановлена частично",
                    _ => "История не обновлена"
                };
                var details = $"Найдено списков: {result.FoundCount}. " +
                              $"Принято: {result.AcceptedCount}. " +
                              $"Отклонено: {result.RejectedCount}.\n" +
                              $"Номера сообщений: {result.OldestMessageId}–{result.NewestMessageId}." +
                              (result.AcceptedCount == 0
                                  ? "\nПредыдущая история сохранена."
                                  : "\nИсторический реестр обновлён. " +
                                    "Актуальная таблица подписок не затронута.") +
                              (string.IsNullOrWhiteSpace(report?.Errors)
                                  ? string.Empty : $"\n\nПричины отклонения:\n{report.Value.Errors}") +
                              (reportError is null ? string.Empty :
                                  $"\nНе удалось прочитать сохранённый отчёт: {reportError}");
                dialog.Finish(title, details);
                await LoadLastParticipantHistoryReportAsync();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                dialog.Finish("Синхронизация остановлена",
                    "Полное восстановление отменено. Ранее сохранённая история не заменена.");
            }
            catch (Exception exception)
            {
                dialog.Finish("Ошибка обновления истории",
                    $"{exception.Message}\nРанее сохранённая история не заменена.");
            }
        };
        var owner = Window.GetWindow(this);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        var mainWindow = owner as ChatPulse.Desktop.MainWindow;
        mainWindow?.ShowDialogOverlay();
        try { dialog.ShowDialog(); }
        finally
        {
            mainWindow?.HideDialogOverlay();
            _historyImportCancellation = null;
            _historyImportRunning = false;
            SetInstagramBusy(false);
            Notify(nameof(CanRebuildParticipantHistory));
        }
    }

    private void TasksTabButton_OnClick(object sender, RoutedEventArgs args)
    {
        SetSubscriptionTableExpanded(false);
        _chatPage = ChatPage.Tasks;
        NotifyPageState();
    }

    private async void SubscriptionsTabButton_OnClick(object sender, RoutedEventArgs args)
    {
        SetSubscriptionTableExpanded(false);
        _chatPage = ChatPage.Subscriptions;
        NotifyPageState();
        SubscriptionsSummary = "Загружаем актуальный список участниц…";
        try { await LoadSubscriptionRowsAsync(); }
        catch (Exception exception)
        {
            SubscriptionsSummary = $"Не удалось загрузить список участниц: {exception.Message}";
        }
    }

    private void ShowLastInstagramCheckDetails_OnClick(object sender, RoutedEventArgs args)
        => ShowSubscriptionInfoDialog(new SubscriptionInfoDialog(LatestSubscriptionBatch));

    private void ShowParticipantListDetails_OnClick(object sender, RoutedEventArgs args)
        => ShowSubscriptionInfoDialog(new SubscriptionInfoDialog(_latestParticipantListSource,
            _subscriptionTileCounts, _latestParticipantListAttempt));

    private void ShowSubscriptionInfoDialog(SubscriptionInfoDialog dialog)
    {
        var owner = Window.GetWindow(this);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        var mainWindow = owner as ChatPulse.Desktop.MainWindow;
        mainWindow?.ShowDialogOverlay();
        try { dialog.ShowDialog(); }
        finally { mainWindow?.HideDialogOverlay(); }
    }

    private void RefreshSubscriptionsButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (IsBusy || _instagramBusy || _synchronization is null || SelectedChat is null) return;
        var selected = SelectedChat;
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        var dialog = new ParticipantListSyncProgressDialog(cancellation);
        var owner = Window.GetWindow(this);
        if (owner is { IsVisible: true }) dialog.Owner = owner;

        dialog.StartRequested += async (_, _) =>
        {
            try
            {
                var result = await _synchronization.RefreshLatestParticipantListAsync(selected.Chat,
                    update =>
                    {
                        if (dialog.Dispatcher.CheckAccess()) dialog.Model.Report(update);
                        else dialog.Dispatcher.Invoke(() => dialog.Model.Report(update));
                    }, cancellation.Token,
                    profileUrl =>
                    {
                        var userName = InstagramActivityProfile.TryGetUserName(profileUrl);
                        var update = new ParticipantListSyncProgress(ParticipantListSyncStage.SAVE,
                            $"Проверяем страницу Instagram @{userName}…");
                        if (dialog.Dispatcher.CheckAccess()) dialog.Model.Report(update);
                        else dialog.Dispatcher.Invoke(() => dialog.Model.Report(update));
                        return _instagramBrowser.ProfileOpensAsync(profileUrl, cancellation.Token)
                            .GetAwaiter().GetResult();
                    });
                if (result.SourceMessageId is null ||
                    result.Status == ParticipantListApplyStatus.UNPARSEABLE_MESSAGE)
                    dialog.Model.Skip(ParticipantListSyncStage.SAVE);
                dialog.Model.Activate(ParticipantListSyncStage.DISPLAY,
                    "Обновляем таблицу участниц…");
                if (ReferenceEquals(selected, SelectedChat))
                    await LoadSubscriptionRowsAsync();

                if (result.SourceMessageSentAtUtc is { } sentAtUtc)
                    dialog.Model.SetSourceMessageDate(sentAtUtc);
                // Found does not mean applied: preserve the currently applied date
                // and explain newer-but-ambiguous or older Telegram messages.
                SubscriptionsSyncMessage = result.Status is
                    ParticipantListApplyStatus.AMBIGUOUS_MATCH or
                    ParticipantListApplyStatus.PROFILE_CHECK_INCONCLUSIVE or
                    ParticipantListApplyStatus.UNPARSEABLE_MESSAGE
                    ? result.OutcomeDescription
                    : string.Empty;
                // Keep the "already current" outcome concise in the sync dialog even
                // if a service implementation returns the older verbose diagnostics.
                var resultDetail = result.OutcomeDescription;
                if (result.Status == ParticipantListApplyStatus.ALREADY_CURRENT)
                {
                    var dateLabel = result.StoredSource is { SentAtUtc: not null } stored
                        ? stored.DateLabel
                        : result.SourceMessageId is { } messageId &&
                          result.SourceMessageSentAtUtc is { } sentAt
                            ? new ParticipantListSourceInfo(messageId, sentAt).DateLabel
                            : null;
                    resultDetail = dateLabel is null
                        ? "Список уже сохранён. Изменений нет."
                        : $"Список от {dateLabel} уже сохранён. Изменений нет.";
                }

                var failed = result.Status is ParticipantListApplyStatus.UNPARSEABLE_MESSAGE or
                    ParticipantListApplyStatus.AMBIGUOUS_MATCH or
                    ParticipantListApplyStatus.PROFILE_CHECK_INCONCLUSIVE;
                dialog.Model.Finish(result.OutcomeTitle, resultDetail,
                    failed ? ParticipantListSyncOutcome.FAILED : ParticipantListSyncOutcome.COMPLETED);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                SubscriptionsSyncMessage = "Синхронизация остановлена.";
                dialog.Model.Finish("Синхронизация остановлена",
                    "Поиск остановлен. Ранее сохранённый список участниц не заменён.",
                    ParticipantListSyncOutcome.STOPPED);
            }
            catch (Exception exception)
            {
                SubscriptionsSyncMessage = $"Не удалось обновить список участниц: {exception.Message}";
                dialog.Model.Finish("Ошибка синхронизации", exception.Message,
                    ParticipantListSyncOutcome.FAILED);
            }
            finally { IsBusy = false; }
        };

        var mainWindow = owner as ChatPulse.Desktop.MainWindow;
        mainWindow?.ShowDialogOverlay();
        try { dialog.ShowDialog(); }
        finally
        {
            mainWindow?.HideDialogOverlay();
            IsBusy = false;
        }
    }

    private void MyActivityTabButton_OnClick(object sender, RoutedEventArgs args)
    {
        _chatPage = ChatPage.MyActivity;
        NotifyPageState();
    }

    private void TaskDateSort_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } selector) return;
        var oldestFirst = selector.SelectedIndex == 1;
        if (_oldestTasksFirst == oldestFirst) return;
        _oldestTasksFirst = oldestFirst;
        UpdateSelectedTasks();
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
                    ? task with { IsCompleted = true }
                    : task).ToArray();
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

    private async void StartTaskExecution_OnClick(object sender, RoutedEventArgs args)
    {
        if (IsBusy || !IsPendingTasksSelected || _reader is null ||
            _synchronization is null || SelectedChat is not { } row) return;

        IsBusy = true;
        try
        {
            var owner = Window.GetWindow(this);
            var dialog = new WorkspaceTaskExecutionDialog(_reader, _synchronization,
                row.Chat, _oldestTasksFirst)
            {
                Owner = owner
            };
            // ShowDialog disables the owner window, including its native minimize button.
            // Keep the owner enabled and use its content overlay to block task interactions
            // while leaving the native window controls available.
            var closed = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult(true);
            var mainWindow = owner as ChatPulse.Desktop.MainWindow;
            mainWindow?.ShowDialogOverlay();
            try
            {
                dialog.Show();
                await closed.Task;
            }
            finally
            {
                mainWindow?.HideDialogOverlay();
            }
        }
        finally
        {
            IsBusy = false;
            // Refresh from SQLite: tasks confirmed by Telegram have moved to Completed.
            // A new run therefore begins with only the remaining pending tasks.
            UpdateSelectedTasks();
            NotifyTaskFilter();
        }
    }

    private async void StartBulkTaskVerification_OnClick(object sender, RoutedEventArgs args)
    {
        if (IsBusy || !IsCompletedTasksSelected || _reader is null ||
            _settings is null || SelectedChat is not { } row) return;

        string account;
        try
        {
            var settings = await _settings.LoadAsync();
            var accounts = await new InstagramOwnAccountStore(_reader.DatabasePath)
                .EnsureFromLegacyAsync(settings.InstagramTargetUserName);
            account = InstagramUserName.Normalize(accounts?.ReceivingUserName ?? string.Empty);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Проверьте основной Instagram-аккаунт в настройках: {exception.Message}",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var owner = Window.GetWindow(this);
        var dialog = new WorkspaceBulkTaskVerificationDialog(
            new WorkspaceBulkTaskVerifier(_reader, _mainInstagramBrowser),
            row.Chat, account, _oldestTasksFirst) { Owner = owner };
        var mainWindow = owner as ChatPulse.Desktop.MainWindow;
        mainWindow?.ShowDialogOverlay();
        try { dialog.ShowDialog(); }
        finally { mainWindow?.HideDialogOverlay(); }

        if (dialog.HiddenCount == 0 && dialog.ProcessedCount == 0) return;
        row.DetectedCount = Math.Max(0, row.DetectedCount - dialog.HiddenCount);
        _totalDetected = Math.Max(0, _totalDetected - dialog.HiddenCount);
        Notify(nameof(TotalDetected));
        Notify(nameof(SelectedChatTaskCount));
        UpdateSelectedTasks();
        NotifyTaskFilter();
    }

    private async void VerifyCompletedTaskButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (IsBusy || _reader is null || _settings is null || SelectedChat is null ||
            _taskFilter != TaskFilter.Completed ||
            sender is not Button { Tag: long messageId }) return;
        var row = SelectedChat;
        if (!_taskCache.TryGetValue(row, out var items)) return;
        var task = items.FirstOrDefault(item => item.MessageId == messageId);
        if (task is not { IsCompleted: true, IsSkipped: false }) return;
        var kind = WorkspaceTaskKindDetector.Detect(task.Text, task.PostUrl);
        if (kind is WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE)
        {
            await VerifySubscriptionTaskAsync(row, messageId, task, kind);
            return;
        }
        if (!InstagramTaskVerificationRules.TryGetPostUrl(task.PostUrl, out var postUrl))
        {
            MessageBox.Show("Для этой проверки нужна прямая ссылка на публикацию или Reel Instagram.",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string initialAccount;
        try
        {
            var settings = await _settings.LoadAsync();
            var own = await new InstagramOwnAccountStore(_reader.DatabasePath)
                .EnsureFromLegacyAsync(settings.InstagramTargetUserName);
            initialAccount = own?.ReceivingUserName ?? string.Empty;
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Не удалось прочитать Instagram-аккаунт: {exception.Message}",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(initialAccount))
        {
            MessageBox.Show("Сначала укажите свой Instagram-аккаунт в настройках.",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var owner = Window.GetWindow(this);
        var dialog = new VerifyCompletedTaskDialog(_mainInstagramBrowser, postUrl, initialAccount,
            task.Text, report => new WorkspaceFailedTaskVerificationStore(_reader.DatabasePath)
                .SaveAsync(row.Chat, messageId, report))
        {
            Owner = owner
        };
        var mainWindow = owner as ChatPulse.Desktop.MainWindow;
        mainWindow?.ShowDialogOverlay();
        InstagramTaskVerificationResult? result;
        try
        {
            result = dialog.ShowDialog() == true ? dialog.VerifiedResult : null;
        }
        finally { mainWindow?.HideDialogOverlay(); }
        if (result is null || !result.CanHide)
        {
            await RefreshFailedVerificationIndicatorsAsync(row, _taskWindowVersion);
            return;
        }

        IsBusy = true;
        try
        {
            await new WorkspaceTaskVerificationStore(_reader.DatabasePath)
                .PublishAsync(row.Chat, messageId, result);
            // The cache contains only visible pages. Update global totals by the known delta.
            row.DetectedCount = Math.Max(0, row.DetectedCount - 1);
            _totalDetected = Math.Max(0, _totalDetected - 1);
            Notify(nameof(TotalDetected));
            Notify(nameof(SelectedChatTaskCount));
            UpdateSelectedTasks();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Проверка прошла, но не удалось сохранить результат: {exception.Message}",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsBusy = false; NotifyTaskFilter(); }
    }

    private async Task VerifySubscriptionTaskAsync(WorkspaceChatRow row, long messageId,
        WorkspaceDetectedTask task, WorkspaceTaskKind kind)
    {
        if (_settings is null || _reader is null) return;
        InstagramOwnAccounts? accounts;
        try
        {
            var settings = await _settings.LoadAsync();
            accounts = await new InstagramOwnAccountStore(_reader.DatabasePath)
                .EnsureFromLegacyAsync(settings.InstagramTargetUserName);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Не удалось прочитать Instagram-аккаунты: {exception.Message}",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (accounts is null || string.IsNullOrWhiteSpace(accounts.ReceivingUserName))
        {
            MessageBox.Show("Укажите основной Instagram-аккаунт в настройках ChatPulse.",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var candidates = InstagramFollowTaskRules.GetVerificationTargets(kind, task.Text, task.PostUrl);
        var owner = Window.GetWindow(this);
        var dialog = new VerifySubscriptionTaskDialog(kind,
            _mainInstagramBrowser, accounts.ReceivingUserName, candidates,
            report => new WorkspaceFailedTaskVerificationStore(_reader.DatabasePath)
                .SaveAsync(row.Chat, messageId, report))
        {
            Owner = owner
        };
        var mainWindow = owner as ChatPulse.Desktop.MainWindow;
        mainWindow?.ShowDialogOverlay();
        InstagramFollowTaskVerification? result;
        try
        {
            result = dialog.ShowDialog() == true ? dialog.VerifiedResult : null;
        }
        finally { mainWindow?.HideDialogOverlay(); }
        if (result?.CanHide != true)
        {
            await RefreshFailedVerificationIndicatorsAsync(row, _taskWindowVersion);
            return;
        }

        IsBusy = true;
        try
        {
            await new WorkspaceSubscriptionTaskVerificationStore(_reader.DatabasePath)
                .PublishAsync(row.Chat, messageId, result);
            row.DetectedCount = Math.Max(0, row.DetectedCount - 1);
            _totalDetected = Math.Max(0, _totalDetected - 1);
            Notify(nameof(TotalDetected));
            Notify(nameof(SelectedChatTaskCount));
            UpdateSelectedTasks();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Проверка прошла, но не удалось сохранить результат: {exception.Message}",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsBusy = false; NotifyTaskFilter(); }
    }

    private async void FailedTaskReportButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_reader is null || SelectedChat is not { } row ||
            _taskFilter != TaskFilter.Completed ||
            sender is not Button { Tag: long messageId }) return;
        try
        {
            var reports = await new WorkspaceFailedTaskVerificationStore(_reader.DatabasePath)
                .ReadAsync(row.Chat, messageId);
            if (reports.Count == 0)
            {
                MessageBox.Show("Для этого задания ещё нет неуспешных отчётов.", "ChatPulse",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var owner = Window.GetWindow(this);
            var dialog = new FailedTaskVerificationReportsDialog(reports) { Owner = owner };
            var mainWindow = owner as ChatPulse.Desktop.MainWindow;
            mainWindow?.ShowDialogOverlay();
            try { dialog.ShowDialog(); }
            finally { mainWindow?.HideDialogOverlay(); }
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Не удалось открыть отчёты проверки: {exception.Message}",
                "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            // The cache contains only visible pages. Update global totals by the known delta.
            row.DetectedCount = Math.Max(0, row.DetectedCount - 1);
            _totalDetected = Math.Max(0, _totalDetected - 1);
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
                // Skipped tasks stay in SQLite. Count changes are independent of the loaded pages.
                var delta = skipped ? -1 : 1;
                row.DetectedCount = Math.Max(0, row.DetectedCount + delta);
                _totalDetected = Math.Max(0, _totalDetected + delta);
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

    private async void ConnectInstagramChatButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (IsBusy || _instagramBusy || _historyImportRunning || _instagramChatConnection is null)
            return;
        var dialog = new ConnectInstagramChatDialog(_instagramChatConnection)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;
        await ReloadAsync();
    }

    private async void CheckReactionsButton_OnClick(object sender, RoutedEventArgs args)
        => await SynchronizeTasksAsync(null);

    private async void SyncSelectedChatButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (SelectedChat is { } selected)
            await SynchronizeTasksAsync(selected.Chat);
    }

    private async Task SynchronizeTasksAsync(ConfiguredChat? onlyChat)
    {
        if (IsBusy || _settings is null || _synchronization is null || _reader is null) return;
        IsBusy = true;
        try
        {
            var state = await _settings.LoadAsync();
            var chats = WorkspaceSynchronizationSelection.Select(state.Chats, onlyChat);
            if (chats.Count == 0)
            {
                ReactionRefreshMessage = "Нет подключённых чатов для синхронизации.";
                return;
            }

            using var cancellation = new CancellationTokenSource();
            var dialog = new TaskSyncProgressDialog(
                onlyChat is null ? "Все подключённые чаты" : "Только выбранный чат",
                chats.Select(chat => chat.TelegramTitle), cancellation);
            var owner = Window.GetWindow(this);
            if (owner is { IsVisible: true }) dialog.Owner = owner;
            dialog.StartRequested += async (_, _) =>
            {
                var updated = 0;
                var errors = new List<string>();
                var stopped = false;
                try
                {
                    var stamps = new WorkspaceSynchronizationStampStore(_reader.DatabasePath);
                    for (var index = 0; index < chats.Count; index++)
                    {
                        if (cancellation.IsCancellationRequested) { stopped = true; break; }
                        var chat = chats[index];
                        var rowIndex = index;
                        dialog.BeginChat(rowIndex);
                        try
                        {
                            var added = await _synchronization.SynchronizeLatestAsync(chat,
                                stage =>
                                {
                                    if (dialog.Dispatcher.CheckAccess()) dialog.ReportStage(rowIndex, stage);
                                    else dialog.Dispatcher.Invoke(() => dialog.ReportStage(rowIndex, stage));
                                }, cancellation.Token);
                            updated += added;
                            await stamps.RecordChatSuccessAsync(chat, DateTimeOffset.UtcNow, cancellation.Token);
                            dialog.CompleteChat(rowIndex, added);
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                        {
                            dialog.StopChat(rowIndex);
                            stopped = true;
                            break;
                        }
                        catch (Exception exception)
                        {
                            errors.Add($"{chat.TelegramTitle}: {exception.Message}");
                            dialog.FailChat(rowIndex, exception.Message);
                        }
                    }
                    if (onlyChat is null && !stopped && errors.Count == 0)
                        await stamps.RecordAllSuccessAsync(chats[0].TelegramUserId, DateTimeOffset.UtcNow, updated);

                    await ReloadAsync();
                    ReactionRefreshMessage = stopped
                        ? $"Синхронизация остановлена. Новых сообщений: {updated}."
                        : errors.Count == 0
                            ? $"Синхронизация завершена. Новых сообщений: {updated}."
                            : $"Часть чатов не синхронизирована: {string.Join("; ", errors)}";
                    dialog.Finish(ReactionRefreshMessage);
                }
                catch (Exception exception)
                {
                    ReactionRefreshMessage = $"Не удалось синхронизировать: {exception.Message}";
                    dialog.Finish(ReactionRefreshMessage);
                }
                finally { IsBusy = false; }
            };

            var mainWindow = owner as ChatPulse.Desktop.MainWindow;
            mainWindow?.ShowDialogOverlay();
            try { dialog.ShowDialog(); }
            finally { mainWindow?.HideDialogOverlay(); }
        }
        catch (Exception exception)
        {
            ReactionRefreshMessage = $"Не удалось начать синхронизацию: {exception.Message}";
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
        if (_page != WorkspacePage.Chat || _chatPage != ChatPage.Subscriptions)
            SetSubscriptionTableExpanded(false);
        if (_page != WorkspacePage.Chat || _chatPage != ChatPage.Tasks)
            SetTaskListExpanded(false);
        Notify(nameof(IsHomeVisible));
        Notify(nameof(IsChatVisible));
        Notify(nameof(IsSettingsVisible));
        Notify(nameof(IsTasksVisible));
        Notify(nameof(IsSubscriptionsVisible));
        Notify(nameof(IsMyActivityVisible));
        NotifyTaskFilter();
        UpdateNavigationVisuals();
    }

    private void NotifyTaskFilter()
    {
        Notify(nameof(IsPendingTasksSelected));
        Notify(nameof(IsCompletedTasksSelected));
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

public sealed class WorkspaceTaskCard(WorkspaceDetectedTask task) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _hasFailedVerification;
    public bool HasFailedVerification
    {
        get => _hasFailedVerification;
        set
        {
            if (_hasFailedVerification == value) return;
            _hasFailedVerification = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFailedVerification)));
        }
    }

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
    private readonly WorkspaceTaskPresentation _presentation =
        WorkspaceTaskPresentation.Create(task.Text, task.CustomEmojiJson);
    private readonly WorkspaceTaskKind _kind =
        WorkspaceTaskKindDetector.Detect(task.Text, task.PostUrl);

    public string KindLabel => _kind switch
    {
        WorkspaceTaskKind.SUBSCRIBE => "Подписка",
        WorkspaceTaskKind.UNSUBSCRIBE => "Отписка",
        _ => "Задание"
    };
    public string KindBackground => _kind switch
    {
        WorkspaceTaskKind.SUBSCRIBE => "#D9F1E0",
        WorkspaceTaskKind.UNSUBSCRIBE => "#FFE5CC",
        _ => "#F0E5FF"
    };
    public string KindForeground => _kind switch
    {
        WorkspaceTaskKind.SUBSCRIBE => "#246F43",
        WorkspaceTaskKind.UNSUBSCRIBE => "#965219",
        _ => "#6D3AAA"
    };

    // Subscription and unsubscription cards deliberately show only clickable targets.
    // The original Telegram text remains available in SQLite and in the source message.
    public bool IsSubscriptionAction => _kind is
        WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE;
    public bool IsRegularTask => !IsSubscriptionAction;
    public string CardBackground => _kind switch
    {
        WorkspaceTaskKind.SUBSCRIBE => "#F2FBF5",
        WorkspaceTaskKind.UNSUBSCRIBE => "#FFF6EC",
        _ => "#FCFAFF"
    };
    public string CardBorderBrush => _kind switch
    {
        WorkspaceTaskKind.SUBSCRIBE => "#CBE9D5",
        WorkspaceTaskKind.UNSUBSCRIBE => "#F3D9B9",
        _ => "#ECE4F5"
    };
    // Instagram post/reel links with tracking query strings and their canonical
    // counterparts identify the same action link. Keep /p/ and /reel/ separate:
    // the UI deliberately provides both entry points when both are present.
    private static string GetActionLinkIdentity(WorkspaceTaskTextPart part)
    {
        var url = part.Url ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Host is not ("instagram.com" or "www.instagram.com"))
            return url;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2 &&
            (string.Equals(segments[0], "p", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(segments[0], "reel", StringComparison.OrdinalIgnoreCase)))
            return $"instagram.com/{segments[0].ToLowerInvariant()}/{segments[1]}";

        return url;
    }

    public IReadOnlyList<WorkspaceTaskTextPart> ActionLinks => WorkspaceTaskLinks.Split(task.Text)
        .Where(part => part.Url is not null)
        .Concat(WorkspaceTaskLinks.Split(task.PostUrl).Where(part => part.Url is not null))
        .DistinctBy(GetActionLinkIdentity, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    // Linked posts in the task body are different from the Telegram message
    // containing the task. The latter is exposed separately by SourceUrl.
    public IReadOnlyList<WorkspaceTaskTextPart> AdditionalActionLinks => ActionLinks
        .Where(part => part.Url is { } url &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Host is ("t.me" or "instagram.com" or "www.instagram.com") &&
            !string.Equals(url, ReelUrl, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(url, FeedPostUrl, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(url, PostUrl, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(url, SourceUrl, StringComparison.OrdinalIgnoreCase))
        .Select(part => new WorkspaceTaskTextPart(part.Text switch
        {
            "Открыть Telegram" => "↗  Пост",
            "Открыть Instagram" => "↗  Instagram",
            "Открыть ссылку" => "↗  Ссылка",
            _ => "↗  " + part.Text.Replace("Открыть ", string.Empty, StringComparison.Ordinal)
        }, part.Url))
        .ToArray();

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
        ? "Сообщение без текста (возможно, содержит медиа)"
        : _presentation.Text;
}