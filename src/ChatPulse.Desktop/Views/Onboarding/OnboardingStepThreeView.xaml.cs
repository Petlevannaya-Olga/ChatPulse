using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Application.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.ViewModels;

namespace ChatPulse.Desktop.Views.Onboarding;

/// <summary>Выбор и проверка чатов. На этом экране подключение не выполняется.</summary>
public partial class OnboardingStepThreeView : UserControl, INotifyPropertyChanged
{
    private string searchText = string.Empty;
    private bool isReview;
    private bool updating;
    private readonly ChatReviewRunGeneration analysisGeneration = new();
    private readonly OnboardingRequestGeneration listGeneration = new();
    private readonly ChatReviewAnalysisCoordinator analysisCoordinator = new();
    private long? rowsOwner;
    private string analysisStatus = "";
    private string currentAnalyzedChatName = string.Empty;
    private bool isAnalyzing;
    private bool analysisCompleted;
    private bool retryOnConnectionRestored;
    private FirstChatSetupViewModel? subscribedViewModel;
    private string connectionStatus = string.Empty;

    public OnboardingStepThreeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public event EventHandler? BackRequested;
    public event Action<IReadOnlyList<PendingChatConnection>>? SynchronizationRequested;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ChatSelectionRow> Rows { get; } = [];
    public ObservableCollection<ChatSelectionRow> SelectedRows { get; } = [];
    public ObservableCollection<ChatAnalysisResultRow> AnalysisRows { get; } = [];
    public string AnalysisStatus { get => analysisStatus; private set { analysisStatus = value; Changed(); } }
    public string CurrentAnalyzedChatName
    {
        get => currentAnalyzedChatName;
        private set
        {
            if (currentAnalyzedChatName == value) return;
            currentAnalyzedChatName = value;
            Changed();
        }
    }
    public bool IsAnalyzing
    {
        get => isAnalyzing;
        private set
        {
            if (isAnalyzing == value) return;
            isAnalyzing = value;
            Changed();
        }
    }
    public string ConnectionStatus
    {
        get => connectionStatus;
        private set { connectionStatus = value; Changed(); }
    }
    public bool CanConnectChats => isReview && analysisCompleted && !IsAnalyzing &&
                                   ViewModel is { CurrentTelegramUserId: > 0 } vm &&
                                   rowsOwner == vm.CurrentTelegramUserId &&
                                   ChatReviewCandidatePolicy.CreateCandidates(vm.CurrentTelegramUserId.Value,
                                       SelectedRows.Select(row => row.Chat).ToArray(), AnalysisRows.ToArray()).Length > 0;
    public bool CanRetryReview => isReview && !IsAnalyzing &&
                                  AnalysisRows.Any(row => row.StatusKind == "Error");
    private void RefreshCanRetryReview() => Changed(nameof(CanRetryReview));
    private void RefreshCanConnect() => Changed(nameof(CanConnectChats));
    public int RecognizedCount => AnalysisRows.Count(row => row.StatusKind == "Success");
    public int AttentionCount => AnalysisRows.Count(row => row.StatusKind == "Warning");
    public int ErrorCount => AnalysisRows.Count(row => row.StatusKind == "Error");

    private void UpdateAnalysisCounts()
    {
        Changed(nameof(RecognizedCount));
        Changed(nameof(AttentionCount));
        Changed(nameof(ErrorCount));
    }
    public ObservableCollection<ChatSelectionRow> VisibleRows { get; } = [];
    private FirstChatSetupViewModel? ViewModel => DataContext as FirstChatSetupViewModel;

    public string SearchText
    {
        get => searchText;
        set
        {
            if (searchText == value) return;
            searchText = value;
            Changed();
            RefreshVisibleRows();
        }
    }

    public string SelectedCountText => $"Выбрано: {Rows.Count(row => row.IsSelected)} / 10";
    public string ListStatus => Rows.Count == 0 ? "Доступных чатов пока нет" : $"Доступно чатов: {Rows.Count}";
    public bool HasSelection => Rows.Any(row => row.IsSelected);
    public Visibility SelectionVisibility => isReview ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ReviewVisibility => isReview ? Visibility.Visible : Visibility.Collapsed;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ReferenceEquals(subscribedViewModel, e.NewValue)) return;
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.TelegramConnectionRestored -= OnTelegramConnectionRestored;
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        // Results and selected peers must not leak into another Telegram account.
        listGeneration.Advance();
        analysisGeneration.Advance();
        isReview = false;
        analysisCompleted = false;
        retryOnConnectionRestored = false;
        IsAnalyzing = false;
        CurrentAnalyzedChatName = string.Empty;
        Rows.Clear();
        SelectedRows.Clear();
        rowsOwner = null;
        VisibleRows.Clear();
        AnalysisRows.Clear();
        Changed(nameof(SelectionVisibility));
        Changed(nameof(ReviewVisibility));
        Changed(nameof(SelectedCountText));
        Changed(nameof(HasSelection));
        Changed(nameof(ListStatus));
        RefreshCanConnect();
        RefreshCanRetryReview();
        UpdateAnalysisCounts();
        subscribedViewModel = e.NewValue as FirstChatSetupViewModel;
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.TelegramConnectionRestored += OnTelegramConnectionRestored;
            subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(FirstChatSetupViewModel.IsBusy) ||
            sender is not FirstChatSetupViewModel vm || vm.IsBusy ||
            !IsVisible || isReview || !ReferenceEquals(ViewModel, vm)) return;

        // EnsureLoadedAsync may return while a previous refresh is still running.
        // Apply its eventual result when the ViewModel finishes loading.
        SyncRows(vm.Chats);
    }

    private void OnTelegramConnectionRestored(object? sender, EventArgs e)
    {
        if (!IsVisible || !isReview) return;
        if (IsAnalyzing)
        {
            // Reconnection may happen while old Telegram requests are still finishing.
            retryOnConnectionRestored = true;
            return;
        }
        if (AnalysisRows.Any(row => row.NeedsConnectionRetry))
            _ = AnalyzeSelectedChatsAsync(retryFailedOnly: true);
    }

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            // Navigation invalidates the UI result, not the in-flight Telegram RPC.
            // Cancelling WTelegram's RPC token can detach its client and falsely
            // report a Telegram connection failure.
            analysisGeneration.Advance();
            listGeneration.Advance();
            IsAnalyzing = false;
            CurrentAnalyzedChatName = string.Empty;
            analysisCompleted = false;
            retryOnConnectionRestored = false;
            RefreshCanConnect();
            RefreshCanRetryReview();
            return;
        }
        if (ViewModel is not { } viewModel) return;
        var request = listGeneration.Advance();
        await viewModel.EnsureLoadedAsync();
        if (!listGeneration.IsCurrent(request) || !IsVisible ||
            !ReferenceEquals(ViewModel, viewModel)) return;
        SyncRows(viewModel.Chats);
        if (isReview && !IsAnalyzing && !analysisCompleted && viewModel.IsTelegramAvailable)
            await AnalyzeSelectedChatsAsync();
    }

    private void SyncRows(IEnumerable<TelegramChatOption> chats)
    {
        var currentOwner = ViewModel?.CurrentTelegramUserId;
        var selectedKeys = rowsOwner == currentOwner
            ? Rows.Where(row => row.IsSelected)
                .Select(row => (row.Chat.PeerId, row.Chat.PeerType)).ToHashSet()
            : new HashSet<(long PeerId, string PeerType)>();
        rowsOwner = currentOwner;
        updating = true;
        Rows.Clear();
        foreach (var chat in chats.Where(chat => ChatTitlePolicy.IsEligible(chat.Title)))
        {
            Rows.Add(new ChatSelectionRow(chat)
            {
                IsSelected = selectedKeys.Contains((chat.PeerId, chat.PeerType))
            });
        }
        updating = false;
        RefreshVisibleRows();
        UpdateSelection();
        Changed(nameof(ListStatus));
    }

    private void RefreshVisibleRows()
    {
        var wasUpdating = updating;
        updating = true;
        try
        {
            VisibleRows.Clear();
            foreach (var row in Rows)
            {
                if (string.IsNullOrWhiteSpace(SearchText) ||
                    row.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                {
                    VisibleRows.Add(row);
                }
            }
        }
        finally
        {
            updating = wasUpdating;
        }
    }

    private void ChatCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (updating || sender is not CheckBox { DataContext: ChatSelectionRow row }) return;
        if (Rows.Count(item => item.IsSelected) > 10)
        {
            row.IsSelected = false;
            // The limit is enforced without removing or changing any other selection.
        }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        SelectedRows.Clear();
        foreach (var row in Rows.Where(row => row.IsSelected)) SelectedRows.Add(row);
        Changed(nameof(SelectedCountText));
        Changed(nameof(HasSelection));
    }

    private async void ShowReview_OnClick(object sender, RoutedEventArgs e)
    {
        if (!HasSelection || SelectedRows.Any(row => !ChatTitlePolicy.IsEligible(row.Chat.Title))) return;
        isReview = true;
        Changed(nameof(SelectionVisibility));
        Changed(nameof(ReviewVisibility));
        await AnalyzeSelectedChatsAsync();
    }

    private async Task AnalyzeSelectedChatsAsync(bool retryFailedOnly = false,
        bool includeOtherErrors = false)
    {
        if (!IsVisible || !isReview || IsAnalyzing || !HasSelection) return;
        if (retryFailedOnly && (AnalysisRows.Count != SelectedRows.Count ||
                                !AnalysisRows.Any(row => ChatReviewRetryPolicy.ShouldRetry(
                                    row, includeOtherErrors)))) return;
        retryOnConnectionRestored = false;
        // A previous RPC may still be finishing. Do not cancel it on navigation:
        // the operation gate serializes calls, and the generation discards its result.
        using var analysisRequest = new CancellationTokenSource();
        var token = analysisRequest.Token;
        var epoch = analysisGeneration.Advance();
        var chosen = SelectedRows.ToArray();
        analysisCompleted = false;
        ConnectionStatus = string.Empty;
        if (!retryFailedOnly)
        {
            AnalysisRows.Clear();
            foreach (var item in chosen)
                AnalysisRows.Add(new ChatAnalysisResultRow(item.Title,
                    item.Chat.PeerId, item.Chat.PeerType));
        }
        RefreshCanConnect();
        RefreshCanRetryReview();
        UpdateAnalysisCounts();
        if (ViewModel is not { } vm) return;
        IsAnalyzing = true;
        RefreshCanRetryReview();
        try
        {
            // The Telegram owner can be reset when moving between onboarding steps.
            // Restore it before attempting to analyze any chat; never treat a missing
            // cached user id as proof of a network outage.
            if (vm.CurrentTelegramUserId is not > 0)
            {
                await vm.EnsureLoadedAsync();
                if (!analysisGeneration.IsCurrent(epoch) || !IsVisible || !isReview) return;
            }
            if (vm.CurrentTelegramUserId is not > 0 ||
                rowsOwner != vm.CurrentTelegramUserId ||
                !ChatReviewCandidatePolicy.AreSelectionsCurrent(
                    chosen.Select(row => row.Chat), vm.Chats))
            {
                // A refreshed Telegram owner or changed chat list invalidates the review.
                // Never analyze or connect a selection from the old snapshot.
                ReturnToSelection_OnClick(this, new RoutedEventArgs());
                Rows.Clear();
                SelectedRows.Clear();
                SyncRows(vm.Chats);
                ConnectionStatus = "Список чатов изменился. Выберите чаты повторно.";
                return;
            }
            var completed = await analysisCoordinator.RunAsync(
                chosen.Select(row => row.Chat).ToArray(), AnalysisRows.ToArray(),
                (chat, cancellationToken) => vm.AnalyzeChatAsync(chat, cancellationToken),
                () => analysisGeneration.IsCurrent(epoch) && IsVisible && isReview &&
                      ReferenceEquals(ViewModel, vm) && rowsOwner == vm.CurrentTelegramUserId,
                retryFailedOnly, includeOtherErrors,
                i =>
                {
                    CurrentAnalyzedChatName = chosen[i].Title;
                    AnalysisStatus = $"Проверка {i + 1} из {chosen.Length}: {chosen[i].Title}";
                },
                () =>
                {
                    UpdateAnalysisCounts();
                    RefreshCanRetryReview();
                },
                token);
            if (completed && analysisGeneration.IsCurrent(epoch) && !token.IsCancellationRequested)
            {
                analysisCompleted = true;
                AnalysisStatus = "Проверка завершена. Результаты предварительные; чаты ещё не подключены.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* user left view */ }
        finally
        {
            // An obsolete analysis must not hide a newer analysis's progress.
            if (analysisGeneration.IsCurrent(epoch))
            {
                IsAnalyzing = false;
                CurrentAnalyzedChatName = string.Empty;
                RefreshCanConnect();
                RefreshCanRetryReview();
                if (retryOnConnectionRestored && IsVisible && isReview && vm.IsTelegramAvailable)
                {
                    retryOnConnectionRestored = false;
                    if (AnalysisRows.Any(row => row.NeedsConnectionRetry))
                        await AnalyzeSelectedChatsAsync(retryFailedOnly: true);
                }
            }
        }
    }

    private async void RetryReview_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanRetryReview || ViewModel is not { } vm) return;
        if (!vm.IsTelegramAvailable)
        {
            ConnectionStatus = "Telegram недоступен. Дождитесь восстановления соединения и повторите проверку.";
            return;
        }

        // Retry is explicit even when the connection monitor never reported an
        // outage (e.g. a stale cached Telegram account after navigation).
        var epoch = analysisGeneration.Advance();
        if (vm.CurrentTelegramUserId is not > 0)
            await vm.RefreshAsync();
        if (analysisGeneration.IsCurrent(epoch) && IsVisible && isReview &&
            ReferenceEquals(ViewModel, vm))
            await AnalyzeSelectedChatsAsync(retryFailedOnly: true, includeOtherErrors: true);
    }

    private void ConnectChats_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanConnectChats || ViewModel is not { CurrentTelegramUserId: > 0 } vm) return;
        var candidates = ChatReviewCandidatePolicy.CreateCandidates(
            vm.CurrentTelegramUserId.Value, SelectedRows.Select(row => row.Chat).ToArray(),
            AnalysisRows.ToArray());
        if (candidates.Length > 0)
            SynchronizationRequested?.Invoke(candidates);
    }

    private void ReturnToSelection_OnClick(object sender, RoutedEventArgs e)
    {
        analysisGeneration.Advance();
        analysisCompleted = false;
        retryOnConnectionRestored = false;
        ConnectionStatus = string.Empty;
        // Do not cancel the in-flight WTelegram RPC when returning to selection.
        // The epoch check ignores its eventual response safely.
        IsAnalyzing = false;
        CurrentAnalyzedChatName = string.Empty;
        AnalysisStatus = string.Empty;
        isReview = false;
        Changed(nameof(SelectionVisibility));
        Changed(nameof(ReviewVisibility));
        RefreshCanConnect();
        RefreshCanRetryReview();
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (isReview) ReturnToSelection_OnClick(sender, e);
        else BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSearch_OnClick(object sender, RoutedEventArgs e)
    {
        SearchText = string.Empty;
        ChatSearchInput.Focus();
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanRefresh: true } viewModel) return;
        var request = listGeneration.Advance();
        await viewModel.RefreshAsync();
        if (!listGeneration.IsCurrent(request) || !IsVisible || isReview ||
            !ReferenceEquals(ViewModel, viewModel)) return;
        SyncRows(viewModel.Chats);
    }

    private void Changed([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed class ChatSelectionRow(TelegramChatOption chat) : INotifyPropertyChanged
{
    private bool isSelected;
    public TelegramChatOption Chat { get; } = chat;
    public string Title => Chat.Title;
    public string Kind => Chat.Kind;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Presentation-only result; no automatic confirmation or settings mutation.</summary>
public sealed class ChatAnalysisResultRow(string title, long? peerId = null,
    string? peerType = null) : INotifyPropertyChanged
{
    public long? PeerId { get; } = peerId;
    public string? PeerType { get; } = peerType;
    private string summary = "Ожидает проверки…";
    private string details = "";
    private string statusKind = "Pending";
    public bool NeedsConnectionRetry { get; private set; }
    public ChatTaskNetwork? VerifiedNetwork { get; private set; }
    public bool IsConnected { get; private set; }
    public bool CanConnect => StatusKind == "Success" && VerifiedNetwork.HasValue && !IsConnected;
    public string StatusKind { get => statusKind; private set { statusKind = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusKind))); } }
    public string Title { get; } = title;
    public string Summary { get => summary; private set { summary = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary))); } }
    public string Details { get => details; private set { details = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Details))); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetResult(ChatActivityAnalysis analysis, int days, int messageCount)
    {
        NeedsConnectionRetry = false;
        var network = analysis.Network switch
        {
            ChatTaskNetwork.INSTAGRAM => "Instagram",
            ChatTaskNetwork.TELEGRAM => "Telegram",
            _ => "Соцсеть не определена"
        };
        VerifiedNetwork = analysis.Classification == ChatActivityClassification.POSSIBLE_ACTIVITY
            ? analysis.Network : null;
        StatusKind = analysis.Classification switch
        {
            ChatActivityClassification.POSSIBLE_ACTIVITY => "Success",
            ChatActivityClassification.CONFLICTING_NETWORKS => "Warning",
            _ => "Warning"
        };
        Summary = analysis.Classification switch
        {
            ChatActivityClassification.POSSIBLE_ACTIVITY => $"Проверка пройдена · {network}",
            ChatActivityClassification.CONFLICTING_NETWORKS => "Противоречивые признаки соцсетей",
            _ => "Недостаточно данных для определения"
        };
        Details = $"Период: {days} дн. · Сообщений: {messageCount} · Заданий: {analysis.TaskCount} · Возможных: {analysis.PossibleTaskCount}";
    }
    public void MarkConnected()
    {
        IsConnected = true;
        Summary = "Чат подключён";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConnect)));
    }
    public void SetError(string error, bool needsConnectionRetry = false)
    {
        NeedsConnectionRetry = needsConnectionRetry;
        VerifiedNetwork = null;
        StatusKind = "Error";
        Summary = "Ошибка проверки";
        Details = error;
    }
}

/// <summary>
/// Invalidates UI results on navigation without cancelling an active Telegram RPC.
/// WTelegram may detach the client if an in-flight request is cancelled.
/// </summary>
public sealed class ChatReviewRunGeneration
{
    private long current;
    public long Advance() => ++current;
    public bool IsCurrent(long generation) => generation == current;
}
