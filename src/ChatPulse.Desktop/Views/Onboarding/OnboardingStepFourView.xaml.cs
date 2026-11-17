using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.ViewModels;
using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Desktop.Views.Onboarding;

public partial class OnboardingStepFourView : UserControl, INotifyPropertyChanged
{
    private ISettingsService? _settings;
    private ChatSynchronizationService? _synchronization;
    private FirstChatSetupViewModel? _chatSetup;
    private bool _isBusy;
    private readonly SemaphoreSlim _periodSaveGate = new(1, 1);
    private readonly OnboardingRequestGeneration _reloadGeneration = new();
    private readonly OnboardingSynchronizationBatchCoordinator _batchCoordinator = new();
    private string _currentActivity = "Выберите период загрузки истории.";

    public OnboardingStepFourView()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? BackRequested;
    public event EventHandler? ContinueRequested;
    public ObservableCollection<SynchronizationChatRow> Chats { get; } = [];
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Notify(); Notify(nameof(CanStart)); Notify(nameof(CanModifyPeriods)); RefreshPhase(); } }
    public bool IsConfigurationPhase => CurrentPhase == SynchronizationScreenPhase.CONFIGURATION;
    public bool IsSynchronizationPhase => CurrentPhase == SynchronizationScreenPhase.SYNCHRONIZING;
    public bool IsHistoryResultPhase => CurrentPhase == SynchronizationScreenPhase.HISTORY_RESULT;
    public SynchronizationScreenPhase CurrentPhase => SynchronizationScreenPhaseResolver.Resolve(
        Chats.Count, Chats.Count(row => !row.NeedsSynchronization), IsBusy);
    public string PhaseSubtitle => CurrentPhase switch
    {
        SynchronizationScreenPhase.SYNCHRONIZING => "Получаем и сохраняем сообщения из Telegram.",
        SynchronizationScreenPhase.HISTORY_RESULT => "Настройте период загрузки истории для каждого чата.",
        _ => "Выберите период загрузки для каждого чата — от 1 до 30 дней."
    };
    public int CompletedChatCount => Chats.Count(row => !row.NeedsSynchronization);
    public int TotalChatCount => Chats.Count;
    public string PrimaryButtonText => Chats.Any(row => row.IsComplete)
        ? "Синхронизировать"
        : "Начать синхронизацию";
    public bool ShowSynchronizationButton => CanStart;
    public bool ShowContinueButton => CanContinue;

    private void RefreshPhase()
    {
        Notify(nameof(IsConfigurationPhase));
        Notify(nameof(IsSynchronizationPhase));
        Notify(nameof(IsHistoryResultPhase));
        Notify(nameof(CurrentPhase));
        Notify(nameof(PhaseSubtitle));
        Notify(nameof(CompletedChatCount));
        Notify(nameof(TotalChatCount));
        Notify(nameof(CanStart));
        Notify(nameof(CanContinue));
        Notify(nameof(ShowSynchronizationButton));
        Notify(nameof(ShowContinueButton));
        Notify(nameof(PrimaryButtonText));
    }
    public bool CanStart => !IsBusy && SynchronizationBatchPlan.SelectPending(Chats).Length > 0;
    public bool CanModifyPeriods => !IsBusy;
    public bool CanContinue => !IsBusy && Chats.Count > 0 &&
        SynchronizationBatchPlan.SelectPending(Chats).Length == 0;
    public string CurrentActivity { get => _currentActivity; private set { _currentActivity = value; Notify(); } }

    public void Initialize(ISettingsService settings, ChatSynchronizationService synchronization,
        FirstChatSetupViewModel chatSetup)
    {
        _settings = settings;
        _synchronization = synchronization;
        _chatSetup = chatSetup;
    }

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!IsVisible)
        {
            _reloadGeneration.Advance();
            return;
        }
        if (_settings is not null && _synchronization is not null && !IsBusy)
            await ReloadAsync(_reloadGeneration.Advance());
    }

    private async Task ReloadAsync(long generation)
    {
        if (_settings is null) return;
        try
        {
            var settings = await _settings.LoadAsync();
            var owner = await _synchronization!.GetAccountIdAsync();
            if (!_reloadGeneration.IsCurrent(generation) || !IsVisible || IsBusy) return;
            var restoredRows = SynchronizationScreenChatPolicy.CreateRows(settings, owner);
            foreach (var previous in Chats)
                previous.PropertyChanged -= OnChatRowChanged;
            Chats.Clear();
            foreach (var row in restoredRows)
                AddChatRow(row);
            CurrentActivity = Chats.Count == 0
                ? "Нет выбранных чатов. Вернитесь к выбору."
                : Chats.All(chat => chat.IsComplete)
                    ? "История сообщений загружена."
                    : "Настройте периоды и начните загрузку.";
            RefreshPhase();
        }
        catch (Exception)
        {
            if (_reloadGeneration.IsCurrent(generation) && IsVisible && !IsBusy)
                CurrentActivity = "Не удалось восстановить настройки синхронизации.";
        }
    }

    private void AddChatRow(SynchronizationChatRow row)
    {
        row.PropertyChanged += OnChatRowChanged;
        Chats.Add(row);
    }

    private void OnChatRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SynchronizationChatRow.HasPeriodChange) or
            nameof(SynchronizationChatRow.IsComplete) or nameof(SynchronizationChatRow.NeedsSynchronization))
            RefreshPhase();
    }

    // Saving a period never starts a Telegram import or connects a pending chat.
    // Periods are persisted independently, so returning to this step restores the selection.
    private async Task PersistPeriodsAsync()
    {
        if (_settings is null) return;
        await _periodSaveGate.WaitAsync();
        try
        {
            foreach (var row in Chats.Where(row => row.CanEditPeriod))
            {
                if (row.IsPendingConnection)
                    await _settings.UpdatePendingChatPeriodAsync(row.Owner, row.PeerId, row.PeerType, row.Days);
                else
                    await _settings.UpdateChatPeriodAsync(row.Owner, row.PeerId, row.PeerType, row.Days);
            }
        }
        finally { _periodSaveGate.Release(); }
    }

    private async void PeriodSlider_OnInteractionCompleted(object sender, RoutedEventArgs args)
    {
        if (!IsVisible || IsBusy || _settings is null) return;
        try { await PersistPeriodsAsync(); }
        catch (Exception exception)
        {
            CurrentActivity = $"Не удалось сохранить выбранный период: {exception.Message}";
        }
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (!CanStart || _settings is null || _synchronization is null) return;
        _reloadGeneration.Advance(); // A late settings read must not replace working rows.
        IsBusy = true;
        try
        {
            // One explicit action for first imports, retries and period changes.
            // The coordinator freezes the batch and guards against double execution.
            await _batchCoordinator.RunAsync(Chats, PersistPeriodsAsync,
                SynchronizeRowAsync, ReloadCompletedRowAsync);
            if (Chats.All(row => !row.NeedsSynchronization))
                CurrentActivity = "История сообщений загружена.";
        }
        catch (Exception exception)
        {
            CurrentActivity = $"Ошибка сохранения периода: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            Notify(nameof(CanStart));
        }
    }

    private async Task ReloadCompletedRowAsync(SynchronizationChatRow row)
    {
        if (_settings is null || _synchronization is null) return;
        row.SetReloading();
        CurrentActivity = $"{row.Title}: обновляем историю за {row.Days} дн.";
        try
        {
            var configured = (await _settings.LoadAsync()).Chats.Single(chat =>
                chat.TelegramUserId == row.Owner && chat.PeerId == row.PeerId &&
                chat.PeerType == row.PeerType);
            var count = await _synchronization.ReloadHistoryAsync(configured, row.Days, phase =>
            {
                row.SetReloadProgress(phase);
                CurrentActivity = $"{row.Title}: {phase}";
            });
            row.SetCompleted(count);
        }
        catch (Exception exception)
        {
            // ReloadHistoryAsync preserves the previous SQLite snapshot on fetch failure.
            // Keep the old successful state; the new period remains pending for retry.
            row.SetReloadFailed(exception.Message);
            CurrentActivity = $"{row.Title}: не удалось обновить историю — {exception.Message}";
        }
    }

    private async Task SynchronizeRowAsync(SynchronizationChatRow row)
    {
        if (_synchronization is null || _settings is null) return;
        try
        {
            if (row.IsPendingConnection)
            {
                row.SetWorking("Подключаем проверенный чат…");
                CurrentActivity = $"{row.Title}: подтверждаем результаты проверки";
                if (_chatSetup is null) throw new InvalidOperationException("Недоступен этап проверки чатов.");
                await _chatSetup.EnsureLoadedAsync();
                var source = _chatSetup.Chats.SingleOrDefault(chat => chat.PeerId == row.PeerId &&
                    chat.PeerType == row.PeerType);
                if (source is null || !ChatTitlePolicy.IsEligible(source.Title))
                    throw new InvalidOperationException("Чат не найден или его название больше не соответствует требованиям.");
                var error = await _chatSetup.SaveVerifiedChatAsync(source, row.Network);
                if (error is not null) throw new InvalidOperationException(error);
                await _settings.UpdateChatPeriodAsync(row.Owner, row.PeerId, row.PeerType, row.Days);
                await _settings.RemovePendingChatAsync(row.Owner, row.PeerId, row.PeerType);
                row.MarkConnected();
            }
            row.SetWorking("Проверяем доступ к Telegram…");
            CurrentActivity = $"{row.Title}: проверка доступа";
            var configured = (await _settings.LoadAsync()).Chats.Single(chat =>
                chat.TelegramUserId == row.Owner && chat.PeerType == row.PeerType && chat.PeerId == row.PeerId);
            var count = await _synchronization.SynchronizeAsync(configured, phase =>
            {
                row.SetWorking(phase);
                CurrentActivity = $"{row.Title}: {phase}";
            });
            row.SetCompleted(count);
        }
        catch (Exception exception)
        {
            row.SetFailed(exception.Message);
            CurrentActivity = $"{row.Title}: ошибка — {exception.Message}";
        }
        RefreshPhase();
        if (Chats.All(chat => chat.IsComplete))
            CurrentActivity = "История сообщений загружена.";
    }

    private async void ContinueButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (!CanContinue) return;
        try
        {
            // Wait for any outstanding slider save before completing onboarding.
            await PersistPeriodsAsync();
            if (CanContinue)
                ContinueRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            CurrentActivity = $"Не удалось сохранить период: {exception.Message}";
            MessageBox.Show(CurrentActivity, "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BackButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (IsBusy) return;
        try
        {
            await PersistPeriodsAsync();
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            CurrentActivity = $"Не удалось сохранить выбранный период: {exception.Message}";
        }
    }

    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed class SynchronizationChatRow : INotifyPropertyChanged
{
    private string _status;
    private string _details;
    private ChatSynchronizationState _state;
    private int _days;
    private int _importedMessageCount;
    private int _loadedDays;
    private bool _pendingConnection;

    public SynchronizationChatRow(ConfiguredChat chat)
    {
        Owner = chat.TelegramUserId;
        PeerId = chat.PeerId;
        PeerType = chat.PeerType;
        Title = chat.TelegramTitle;
        Network = chat.SocialNetwork;
        _loadedDays = chat.HistoryDays is >= 1 and <= 30 ? chat.HistoryDays : 7;
        _days = chat.RequestedHistoryDays is >= 1 and <= 30
            ? chat.RequestedHistoryDays.Value : _loadedDays;
        _state = chat.SynchronizationState;
        _importedMessageCount = chat.ImportedMessageCount;
        _status = _state switch
        {
            ChatSynchronizationState.COMPLETED => "История сообщений загружена",
            ChatSynchronizationState.FAILED => "Ошибка загрузки — можно повторить",
            ChatSynchronizationState.RUNNING => "Предыдущая загрузка прервана — можно повторить",
            _ => "Ожидает загрузки"
        };
        _details = chat.SynchronizationError ??
            (_state == ChatSynchronizationState.COMPLETED ? $"Сообщений: {_importedMessageCount}" : string.Empty);
        if (HasPeriodChange)
        {
            _status = "Период изменён";
            _details = "История будет обновлена после нажатия «Синхронизировать».";
        }
    }

    public SynchronizationChatRow(PendingChatConnection pending)
    {
        Owner = pending.TelegramUserId;
        PeerId = pending.PeerId;
        PeerType = pending.PeerType;
        Title = pending.TelegramTitle;
        Network = pending.SocialNetwork;
        _days = pending.HistoryDays;
        _loadedDays = _days;
        _pendingConnection = true;
        _state = ChatSynchronizationState.PENDING;
        _status = "Ожидает подключения";
        _details = "Проверка пройдена. Подключение начнётся после выбора периода.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsPendingConnection => _pendingConnection;
    public string Network { get; } = string.Empty;
    public void MarkConnected()
    {
        _pendingConnection = false;
        Notify(nameof(IsPendingConnection));
    }
    public long Owner { get; }
    public long PeerId { get; }
    public string PeerType { get; }
    public string Title { get; }
    public int[] AvailableDays { get; } = Enumerable.Range(1, 30).ToArray();
    public int Days
    {
        get => _days;
        set
        {
            if (!CanEditPeriod || value is < 1 or > 30 || _days == value) return;
            _days = value;
            Notify();
            if (IsComplete)
            {
                Status = HasPeriodChange ? "Период изменён" : "История сообщений загружена";
                Details = HasPeriodChange
                    ? "История будет обновлена после нажатия «Синхронизировать»."
                    : $"Сообщений: {_importedMessageCount}";
            }
            Notify(nameof(HasPeriodChange));
            Notify(nameof(NeedsSynchronization));
        }
    }
    public bool HasPeriodChange => IsComplete && _days != _loadedDays;
    public bool NeedsSynchronization => !IsComplete || HasPeriodChange; 
    public void ConfirmPeriod()
    {
        _loadedDays = _days;
        Notify(nameof(HasPeriodChange));
        Notify(nameof(NeedsSynchronization));
    }
    public string Status { get => _status; private set { _status = value; Notify(); } }
    public string Details { get => _details; private set { _details = value; Notify(); } }
    public int ImportedMessageCount => _importedMessageCount;
    public bool IsComplete => _state == ChatSynchronizationState.COMPLETED;
    public bool CanRetry => _state is ChatSynchronizationState.FAILED or ChatSynchronizationState.RUNNING;
    public bool CanEditPeriod => _state is ChatSynchronizationState.PENDING or ChatSynchronizationState.COMPLETED;

    public void SetWorking(string message)
    {
        _state = ChatSynchronizationState.RUNNING;
        Status = "Выполняется синхронизация";
        Details = message;
        UpdateState();
    }
    public void SetCompleted(int count)
    {
        _state = ChatSynchronizationState.COMPLETED;
        _loadedDays = _days;
        _importedMessageCount = count;
        Notify(nameof(ImportedMessageCount));
        Status = "История сообщений загружена";
        Details = $"Сообщений: {count}";
        UpdateState();
    }
    public void SetFailed(string message)
    {
        _state = ChatSynchronizationState.FAILED;
        Status = "Ошибка загрузки — можно повторить";
        Details = message;
        UpdateState();
    }
    public void SetReloading()
    {
        // A reload does not invalidate the previously imported snapshot.
        Status = "Обновляем историю";
        Details = "Загружаем сообщения за новый период…";
    }
    public void SetReloadProgress(string phase) => Details = phase;
    public void SetReloadFailed(string message)
    {
        Status = "Не удалось обновить историю";
        Details = $"Прежняя история сохранена. {message}";
        // _state and _loadedDays stay unchanged so the common button can retry.
        UpdateState();
    }
    private void UpdateState()
    {
        Notify(nameof(IsComplete));
        Notify(nameof(CanRetry));
        Notify(nameof(CanEditPeriod));
        Notify(nameof(HasPeriodChange));
        Notify(nameof(NeedsSynchronization));
    }
    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// Produces the immutable work list for one explicit click. Successfully imported chats
/// without period changes must never be requested again by this batch.
/// </summary>
public static class SynchronizationBatchPlan
{
    public static SynchronizationChatRow[] SelectPending(IEnumerable<SynchronizationChatRow> chats)
        => chats.Where(row => row.NeedsSynchronization).ToArray();
}
