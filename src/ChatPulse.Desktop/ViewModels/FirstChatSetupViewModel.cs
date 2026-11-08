using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChatPulse.Application.Features.Telegram.GetChats;
using ChatPulse.Application.Features.Chats.Save;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.Settings;
using ChatPulse.Shared.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ChatPulse.Desktop.ViewModels;

/// <summary>Состояние третьего шага. Telegram читается через CQRS, настройки — через SettingsService.</summary>
public sealed class FirstChatSetupViewModel(
    IServiceScopeFactory scopeFactory,
    ISettingsService settings) : INotifyPropertyChanged
{
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
    public bool CanRefresh => !IsBusy;
    public bool CanSave => !IsBusy && _telegramUserId is > 0 && SelectedChat is not null
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
            foreach (var chat in snapshot.Chats)
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
