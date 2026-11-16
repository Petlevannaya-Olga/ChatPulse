using ChatPulse.Application.Features.Chats.Analyze;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChatPulse.Application.Features.Telegram.GetChats;
using ChatPulse.Application.Services;
using ChatPulse.Application.Features.Chats.Save;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using ChatPulse.Shared.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ChatPulse.Desktop.ViewModels;

/// <summary>Состояние третьего шага. Telegram читается через CQRS, настройки — через SettingsService.</summary>
public sealed class FirstChatSetupViewModel : INotifyPropertyChanged
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ISettingsService settings;
    private readonly ITelegramConnectionState networkState;
    private bool wasTelegramAvailable;

    public FirstChatSetupViewModel(IServiceScopeFactory scopeFactory,
        ISettingsService settings, ITelegramConnectionState networkState)
    {
        this.scopeFactory = scopeFactory;
        this.settings = settings;
        this.networkState = networkState;
        wasTelegramAvailable = networkState.CanUseTelegram;
        networkState.StateChanged += OnNetworkStateChanged;
    }
    private TelegramChatOption? _selectedChat;
    private string _displayName = string.Empty;
    private string? _socialNetwork;
    private DateTime? _startDate = DateTime.Today;
    private string _error = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;
    private bool _isSaved;
    private bool _initialized;
    private bool _reloadAfterCurrent;
    private long _viewEpoch;
    private long? _telegramUserId;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TelegramConnectionRestored;

    public bool IsTelegramAvailable => networkState.CanUseTelegram;

    // No network RPC is started when the global monitor reports an outage.
    // UI updates are dispatched by the monitor on the WPF thread.
    private void OnNetworkStateChanged()
    {
        var available = networkState.CanUseTelegram;
        var restored = available && !wasTelegramAvailable;
        wasTelegramAvailable = available;
        Changed(nameof(CanRefresh));
        Changed(nameof(CanSave));
        Changed(nameof(IsTelegramAvailable));
        if (available && _initialized && Chats.Count == 0)
            _initialized = false;
        if (restored)
            TelegramConnectionRestored?.Invoke(this, EventArgs.Empty);
    }

    public long? CurrentTelegramUserId => _telegramUserId;

    public ObservableCollection<TelegramChatOption> Chats { get; } = [];
    public IReadOnlyList<string> SocialNetworks { get; } = ["Instagram", "Telegram", "TikTok"];

    public TelegramChatOption? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (Equals(_selectedChat, value)) return;
            _selectedChat = value;
            Changed();
            DisplayName = value?.Title ?? string.Empty;
            IsSaved = false;
            Changed(nameof(CanSave));
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            Changed();
            IsSaved = false;
            Changed(nameof(CanSave));
        }
    }

    public string? SocialNetwork
    {
        get => _socialNetwork;
        set
        {
            if (_socialNetwork == value) return;
            _socialNetwork = value;
            Changed();
            IsSaved = false;
            Changed(nameof(CanSave));
        }
    }

    public DateTime? StartDate
    {
        get => _startDate;
        set
        {
            if (_startDate == value) return;
            _startDate = value;
            Changed();
            IsSaved = false;
            Changed(nameof(CanSave));
        }
    }

    public string Error
    {
        get => _error;
        private set { _error = value; Changed(); Changed(nameof(HasError)); }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; Changed(); }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            Changed();
            Changed(nameof(CanSave));
            Changed(nameof(CanRefresh));
        }
    }
    public bool IsSaved
    {
        get => _isSaved;
        private set { _isSaved = value; Changed(); }
    }
    public bool CanRefresh => !IsBusy && networkState.CanUseTelegram;
    public bool CanSave => networkState.CanUseTelegram && !IsBusy && _telegramUserId is > 0 && SelectedChat is { } chat && ChatTitlePolicy.IsEligible(chat.Title)
        && !string.IsNullOrWhiteSpace(DisplayName)
        && SocialNetwork is not null && StartDate.HasValue;

    /// <summary>
    /// Сбрасывает только экран и кэш списка, не удаляя конфигурацию других аккаунтов.
    /// Запоздавший ответ старой загрузки будет проигнорирован.
    /// </summary>
    public void Invalidate()
    {
        _viewEpoch++;
        _initialized = false;
        _telegramUserId = null;
        Chats.Clear();
        SelectedChat = null;
        DisplayName = string.Empty;
        SocialNetwork = null;
        StartDate = DateTime.Today;
        IsSaved = false;
        Error = string.Empty;
        Status = string.Empty;
        Changed(nameof(CanSave));
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        if (IsBusy)
        {
            _reloadAfterCurrent = true;
            return;
        }
        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!networkState.CanUseTelegram)
        {
            _initialized = false;
            Status = string.Empty;
            Error = "Telegram временно недоступен. Дождитесь восстановления соединения.";
            return;
        }
        if (IsBusy) return;
        var epoch = _viewEpoch;
        var previousOwner = _telegramUserId;
        var previousSelection = SelectedChat;
        _initialized = true;
        IsBusy = true;
        Error = string.Empty;
        Status = "Загружаем список чатов из Telegram…";
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                IQueryHandler<TelegramChatsSnapshot, GetTelegramChatsQuery>>();
            var result = await handler.Handle(new GetTelegramChatsQuery(), cancellationToken);
            if (epoch != _viewEpoch) return;
            if (result.IsFailure)
            {
                _telegramUserId = null;
                Chats.Clear();
                SelectedChat = null;
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                Status = string.Empty;
                _initialized = false;
                return;
            }

            var snapshot = result.Value;
            if (snapshot.TelegramUserId <= 0)
            {
                _initialized = false;
                Error = "Не удалось определить аккаунт Telegram. Подключите его заново.";
                return;
            }

            // Читаем конфигурацию только после получения подтверждённого user ID.
            // Старые записи без владельца (TelegramUserId = 0) не присваиваем
            // новому аккаунту автоматически.
            var appSettings = await settings.LoadAsync(cancellationToken);
            if (epoch != _viewEpoch) return;
            var saved = appSettings.Chats?.FirstOrDefault(chat =>
                chat.TelegramUserId == snapshot.TelegramUserId);
            var hasUnownedChats = appSettings.Chats?.Any(chat => chat.TelegramUserId <= 0) == true;
            _telegramUserId = snapshot.TelegramUserId;
            Chats.Clear();
            foreach (var chat in snapshot.Chats.Where(chat => ChatTitlePolicy.IsEligible(chat.Title)))
                Chats.Add(chat);

            var candidate = saved is not null
                ? Chats.FirstOrDefault(chat => chat.PeerId == saved.PeerId && chat.PeerType == saved.PeerType)
                : previousOwner == snapshot.TelegramUserId && previousSelection is not null
                    ? Chats.FirstOrDefault(chat => chat.PeerId == previousSelection.PeerId &&
                                                   chat.PeerType == previousSelection.PeerType)
                    : null;
            SelectedChat = candidate;

            if (saved is not null && SelectedChat is not null)
            {
                DisplayName = saved.DisplayName;
                SocialNetwork = saved.SocialNetwork;
                StartDate = saved.StartDate.ToDateTime(TimeOnly.MinValue);
                IsSaved = true;
            }
            else if (saved is not null)
            {
                Error = "Ранее выбранный чат сейчас недоступен. Проверьте, что аккаунт состоит в нём.";
            }

            Status = Chats.Count == 0
                ? "В аккаунте пока не найдено доступных групп или каналов."
                : $"Доступно чатов: {Chats.Count}";
            if (hasUnownedChats && saved is null)
            {
                Status += " Старые настройки без привязки к аккаунту сохранены отдельно; выберите чат заново.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initialized = false;
        }
        catch (Exception) when (epoch != _viewEpoch)
        {
            // Ответ от уже закрытого или сменившего аккаунт экрана не применяем.
        }
        catch (Exception)
        {
            _initialized = false;
            Error = "Не удалось загрузить список чатов. Проверьте подключение и нажмите «Обновить».";
            Status = string.Empty;
        }
        finally
        {
            IsBusy = false;
            if (_reloadAfterCurrent && !_initialized)
            {
                _reloadAfterCurrent = false;
                await RefreshAsync(cancellationToken);
            }
        }
    }

    /// <summary>Preliminary analysis only. No settings updates and no read acknowledgements.</summary>
    public async Task<(ChatActivityAnalysis Analysis, int Days, int Count)> AnalyzeChatAsync(
        TelegramChatOption chat, CancellationToken cancellationToken = default)
    {
        if (!ChatTitlePolicy.IsEligible(chat.Title))
            throw new ArgumentException("Название чата должно содержать отдельное слово «пост».", nameof(chat));
        if (!networkState.CanUseTelegram)
            throw new TelegramAuthenticationException("telegram.network_unavailable",
                "Нет подключения к Telegram. Дождитесь восстановления связи.");
        var expectedUserId = _telegramUserId;
        if (expectedUserId is not > 0)
            throw new TelegramAuthenticationException("telegram.not_authorized",
                "Не удалось подтвердить Telegram-аккаунт для анализа.");
        using var scope = scopeFactory.CreateScope();
        var operationGate = scope.ServiceProvider.GetRequiredService<ITelegramOperationGate>();
        using var operation = await operationGate.EnterAsync(cancellationToken);
        var telegram = scope.ServiceProvider.GetRequiredService<ITelegramAuthenticationClient>();
        if (await telegram.GetAuthorizedUserIdAsync(cancellationToken) != expectedUserId)
            throw new TelegramAuthenticationException("telegram.account_changed",
                "Telegram-аккаунт изменился. Обновите список чатов.");
        var lastDay = await telegram.GetRecentChatMessagesAsync(chat, 1, cancellationToken);
        var analysis = ChatActivityAnalyzer.Analyze(lastDay.Select(message =>
            new ChatActivityMessage(message.MessageId, message.Text)));
        if (analysis.Classification != ChatActivityClassification.INSUFFICIENT_DATA)
            return (analysis, 1, lastDay.Count);

        var lastWeek = await telegram.GetRecentChatMessagesAsync(chat, 7, cancellationToken);
        analysis = ChatActivityAnalyzer.Analyze(lastWeek.Select(message =>
            new ChatActivityMessage(message.MessageId, message.Text)));
        return (analysis, 7, lastWeek.Count);
    }

    /// <summary>Only called with a recognized result; the handler independently verifies it again.</summary>
    public async Task<string?> SaveVerifiedChatAsync(TelegramChatOption chat, string network,
        CancellationToken cancellationToken = default)
    {
        if (!ChatTitlePolicy.IsEligible(chat.Title) || network is not ("Instagram" or "Telegram"))
            return "Чат не соответствует требованиям подключения.";
        if (!networkState.CanUseTelegram || _telegramUserId is not > 0)
            return "Telegram недоступен. Повторите авторизацию или обновите список чатов.";
        if (!Chats.Any(candidate => candidate.PeerId == chat.PeerId && candidate.PeerType == chat.PeerType))
            return "Чат отсутствует в актуальном списке Telegram.";
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<bool, SaveChatCommand>>();
            var command = new SaveChatCommand(chat.PeerId, chat.PeerType, chat.Title, chat.Title,
                network, DateOnly.FromDateTime(DateTime.Today), _telegramUserId.Value);
            var outcome = await handler.Handle(command, cancellationToken);
            return outcome.IsFailure
                ? outcome.Error.Errors?.FirstOrDefault()?.Message ?? outcome.Error.Message
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return "Подключение отменено.";
        }
        catch (TelegramAuthenticationException exception)
        {
            return exception.Message;
        }
        catch (Exception)
        {
            return "Не удалось сохранить настройки чата.";
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave) return;
        var epoch = _viewEpoch;
        var expectedUserId = _telegramUserId!.Value;
        IsBusy = true;
        Error = string.Empty;
        try
        {
            var chat = SelectedChat!;
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                ICommandHandler<bool, SaveChatCommand>>();
            var command = new SaveChatCommand(
                chat.PeerId,
                chat.PeerType,
                chat.Title,
                DisplayName,
                SocialNetwork!,
                DateOnly.FromDateTime(StartDate!.Value),
                expectedUserId);
            var result = await handler.Handle(command, cancellationToken);
            if (epoch != _viewEpoch) return;
            if (result.IsFailure)
            {
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return;
            }
            IsSaved = true;
            Status = "Первый чат сохранён. Загрузка заданий будет следующим этапом.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /*ignore*/ }
        catch (Exception) when (epoch != _viewEpoch) { /* устаревший ответ */ }
        catch (Exception)
        {
            Error = "Не удалось сохранить чат в settings.json. Проверьте доступ к папке ChatPulse.";
        }
        finally { IsBusy = false; }
    }

    private void Changed([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
