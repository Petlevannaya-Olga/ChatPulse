using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Application.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.ViewModels;

namespace ChatPulse.Desktop.Views.Onboarding;

/// <summary>Первый UI-этап массового подключения. Анализ и сохранение ещё не реализованы.</summary>
public partial class OnboardingStepThreeView : UserControl, INotifyPropertyChanged
{
    private string searchText = string.Empty;
    private bool isReview;
    private bool updating;
    private CancellationTokenSource? analysisCancellation;
    private long analysisEpoch;
    private string analysisStatus = "";
    private string currentAnalyzedChatName = string.Empty;
    private bool isAnalyzing;
    private bool isConnecting;
    private bool analysisCompleted;
    private string connectionStatus = string.Empty;

    public OnboardingStepThreeView()
    {
        InitializeComponent();
        // Keep an explicit collection for the XAML ItemsControl: unlike a view
        // proxy, this is always refreshed after the Telegram snapshot arrives.
    }

    public event EventHandler? BackRequested;
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
    public bool CanConnectChats => isReview && analysisCompleted && !IsAnalyzing && !isConnecting &&
                                   AnalysisRows.Any(row => row.CanConnect && !row.IsConnected);
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

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            analysisCancellation?.Cancel();
            IsAnalyzing = false;
            CurrentAnalyzedChatName = string.Empty;
            analysisCompleted = false;
            RefreshCanConnect();
            return;
        }
        if (ViewModel is not { } viewModel) return;
        await viewModel.EnsureLoadedAsync();
        SyncRows(viewModel.Chats);
    }

    private void SyncRows(IEnumerable<TelegramChatOption> chats)
    {
        var selectedKeys = Rows.Where(row => row.IsSelected)
            .Select(row => (row.Chat.PeerId, row.Chat.PeerType)).ToHashSet();
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
        analysisCancellation?.Cancel();
        analysisCancellation = new CancellationTokenSource();
        var token = analysisCancellation.Token;
        var epoch = ++analysisEpoch;
        var chosen = SelectedRows.ToArray();
        analysisCompleted = false;
        ConnectionStatus = string.Empty;
        AnalysisRows.Clear();
        RefreshCanConnect();
        foreach (var item in chosen)
            AnalysisRows.Add(new ChatAnalysisResultRow(item.Title));
        UpdateAnalysisCounts();
        isReview = true;
        Changed(nameof(SelectionVisibility));
        Changed(nameof(ReviewVisibility));
        if (ViewModel is not { } vm) return;
        IsAnalyzing = true;
        try
        {
            for (var i = 0; i < chosen.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                CurrentAnalyzedChatName = chosen[i].Title;
                AnalysisStatus = $"Проверка {i + 1} из {chosen.Length}: {chosen[i].Title}";
                try
                {
                    var (result, days, count) = await vm.AnalyzeChatAsync(chosen[i].Chat, token);
                    if (epoch != analysisEpoch || token.IsCancellationRequested) return;
                    AnalysisRows[i].SetResult(result, days, count);
                    UpdateAnalysisCounts();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (TelegramAuthenticationException exception)
                {
                    if (epoch != analysisEpoch || token.IsCancellationRequested) return;
                    AnalysisRows[i].SetError(exception.Message);
                    UpdateAnalysisCounts();
                }
                catch (Exception)
                {
                    if (epoch != analysisEpoch || token.IsCancellationRequested) return;
                    AnalysisRows[i].SetError("Не удалось проверить этот чат. Повторите анализ.");
                    UpdateAnalysisCounts();
                }
            }
            if (epoch == analysisEpoch && !token.IsCancellationRequested)
            {
                analysisCompleted = true;
                AnalysisStatus = "Проверка завершена. Результаты предварительные; чаты ещё не подключены.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* user left view */ }
        finally
        {
            // An older cancelled analysis must not hide a newer analysis's progress.
            if (epoch == analysisEpoch)
            {
                IsAnalyzing = false;
                CurrentAnalyzedChatName = string.Empty;
                RefreshCanConnect();
            }
        }
    }

    private async void ConnectChats_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanConnectChats || ViewModel is not { } vm) return;
        var candidates = SelectedRows.Zip(AnalysisRows)
            .Where(pair => pair.Second.CanConnect && pair.Second.VerifiedNetwork.HasValue)
            .ToArray();
        if (candidates.Length == 0) return;

        isConnecting = true;
        RefreshCanConnect();
        var connected = 0;
        try
        {
            foreach (var (chat, result) in candidates)
            {
                // The application handler re-checks Telegram and evidence before writing settings.
                var network = result.VerifiedNetwork == ChatTaskNetwork.INSTAGRAM ? "Instagram" : "Telegram";
                var error = await vm.SaveVerifiedChatAsync(chat.Chat, network);
                if (error is not null)
                {
                    ConnectionStatus = $"Не удалось подключить «{chat.Title}»: {error}";
                    return;
                }
                result.MarkConnected();
                connected++;
                ConnectionStatus = $"Подключено: {connected} из {candidates.Length}";
            }
        }
        finally
        {
            isConnecting = false;
            RefreshCanConnect();
        }
    }

    private void ReturnToSelection_OnClick(object sender, RoutedEventArgs e)
    {
        analysisEpoch++;
        analysisCompleted = false;
        ConnectionStatus = string.Empty;
        analysisCancellation?.Cancel();
        IsAnalyzing = false;
        CurrentAnalyzedChatName = string.Empty;
        AnalysisStatus = string.Empty;
        isReview = false;
        Changed(nameof(SelectionVisibility));
        Changed(nameof(ReviewVisibility));
        RefreshCanConnect();
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
        if (ViewModel is not { } viewModel) return;
        await viewModel.RefreshAsync();
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
public sealed class ChatAnalysisResultRow(string title) : INotifyPropertyChanged
{
    private string summary = "Ожидает проверки…";
    private string details = "";
    private string statusKind = "Pending";
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
            ChatActivityClassification.POSSIBLE_ACTIVITY => $"Возможный чат активности · {network}",
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
    public void SetError(string error)
    {
        VerifiedNetwork = null;
        StatusKind = "Error";
        Summary = "Ошибка проверки";
        Details = error;
    }
}
