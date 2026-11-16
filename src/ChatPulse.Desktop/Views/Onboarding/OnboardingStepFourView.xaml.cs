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
    private string _currentActivity = "Выберите период загрузки истории.";

    public OnboardingStepFourView()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? BackRequested;
    public ObservableCollection<SynchronizationChatRow> Chats { get; } = [];
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Notify(); Notify(nameof(CanStart)); } }
    public bool CanStart => !IsBusy && Chats.Any(row => !row.IsComplete);
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
        if (IsVisible && _settings is not null && _synchronization is not null)
            await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_settings is null) return;
        try
        {
            var settings = await _settings.LoadAsync();
            var owner = await _synchronization!.GetAccountIdAsync();
            Chats.Clear();
            foreach (var chat in settings.Chats.Where(chat => chat.TelegramUserId == owner))
                Chats.Add(new SynchronizationChatRow(chat));
            foreach (var pending in (settings.PendingChats ?? []).Where(chat => chat.TelegramUserId == owner &&
                         !Chats.Any(saved => saved.PeerId == chat.PeerId && saved.PeerType == chat.PeerType)))
                Chats.Add(new SynchronizationChatRow(pending));
            CurrentActivity = Chats.Count == 0
                ? "Нет выбранных чатов. Вернитесь к выбору."
                : Chats.All(chat => chat.IsComplete)
                    ? "История всех чатов загружена. Формирование заданий будет следующим этапом."
                    : "Настройте периоды и начните загрузку.";
            Notify(nameof(CanStart));
        }
        catch (Exception)
        {
            CurrentActivity = "Не удалось восстановить настройки синхронизации.";
        }
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (!CanStart || _settings is null || _synchronization is null) return;
        IsBusy = true;
        try
        {
            foreach (var row in Chats.Where(row => !row.IsComplete).ToArray())
            {
                if (row.CanEditPeriod)
                {
                    if (row.IsPendingConnection)
                        await _settings.UpdatePendingChatPeriodAsync(row.Owner, row.PeerId, row.PeerType, row.Days);
                    else
                        await _settings.UpdateChatPeriodAsync(row.Owner, row.PeerId, row.PeerType, row.Days);
                }
            }
            foreach (var row in Chats.Where(row => !row.IsComplete).ToArray())
                await SynchronizeRowAsync(row);
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

    private async void RetryButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: SynchronizationChatRow row } || IsBusy || !row.CanRetry) return;
        IsBusy = true;
        try { await SynchronizeRowAsync(row); }
        finally { IsBusy = false; Notify(nameof(CanStart)); }
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
        Notify(nameof(CanStart));
        if (Chats.All(chat => chat.IsComplete))
            CurrentActivity = "История всех чатов загружена. Формирование заданий будет следующим этапом.";
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (!IsBusy) BackRequested?.Invoke(this, EventArgs.Empty);
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
    private bool _pendingConnection;

    public SynchronizationChatRow(ConfiguredChat chat)
    {
        Owner = chat.TelegramUserId;
        PeerId = chat.PeerId;
        PeerType = chat.PeerType;
        Title = chat.TelegramTitle;
        _days = chat.HistoryDays is >= 1 and <= 30 ? chat.HistoryDays : 7;
        _state = chat.SynchronizationState;
        _importedMessageCount = chat.ImportedMessageCount;
        _status = _state switch
        {
            ChatSynchronizationState.COMPLETED => "История загружена",
            ChatSynchronizationState.FAILED => "Ошибка загрузки — можно повторить",
            ChatSynchronizationState.RUNNING => "Предыдущая загрузка прервана — можно повторить",
            _ => "Ожидает загрузки"
        };
        _details = chat.SynchronizationError ??
            (_state == ChatSynchronizationState.COMPLETED ? $"Сообщений: {_importedMessageCount}" : string.Empty);
    }

    public SynchronizationChatRow(PendingChatConnection pending)
    {
        Owner = pending.TelegramUserId;
        PeerId = pending.PeerId;
        PeerType = pending.PeerType;
        Title = pending.TelegramTitle;
        Network = pending.SocialNetwork;
        _days = pending.HistoryDays;
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
    public int Days { get => _days; set { if (CanEditPeriod && value is >= 1 and <= 30) { _days = value; Notify(); } } }
    public string Status { get => _status; private set { _status = value; Notify(); } }
    public string Details { get => _details; private set { _details = value; Notify(); } }
    public bool IsComplete => _state == ChatSynchronizationState.COMPLETED;
    public bool CanRetry => _state is ChatSynchronizationState.FAILED or ChatSynchronizationState.RUNNING;
    public bool CanEditPeriod => _state == ChatSynchronizationState.PENDING;

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
        Status = "История загружена";
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
    private void UpdateState()
    {
        Notify(nameof(IsComplete));
        Notify(nameof(CanRetry));
        Notify(nameof(CanEditPeriod));
    }
    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
