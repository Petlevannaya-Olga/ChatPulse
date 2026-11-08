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
    public bool CanSave => !IsBusy && SelectedChat is not null
        && !string.IsNullOrWhiteSpace(DisplayName)
        && SocialNetwork is not null && StartDate.HasValue;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || IsBusy) return;
        _initialized = true;
        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = string.Empty;
        Status = "Загружаем список чатов из Telegram…";
        try
        {
            var saved = (await settings.LoadAsync(cancellationToken)).Chats?.FirstOrDefault();
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<
                IQueryHandler<IReadOnlyList<TelegramChatOption>, GetTelegramChatsQuery>>();
            var result = await handler.Handle(new GetTelegramChatsQuery(), cancellationToken);
            if (result.IsFailure)
            {
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                Status = string.Empty;
                _initialized = false; // разрешаем повторное открытие и обновление
                return;
            }

            Chats.Clear();
            foreach (var chat in result.Value)
                Chats.Add(chat);

            // При возвращении на экран восстанавливаем ранее выбранный чат.
            var candidate = saved is null ? SelectedChat : Chats.FirstOrDefault(chat =>
                chat.PeerId == saved.PeerId && chat.PeerType == saved.PeerType);
            SelectedChat = candidate is null ? null : Chats.FirstOrDefault(chat =>
                chat.PeerId == candidate.PeerId && chat.PeerType == candidate.PeerType);

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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initialized = false;
        }
        catch (Exception)
        {
            _initialized = false;
            Error = "Не удалось загрузить список чатов. Проверьте подключение и нажмите «Обновить».";
            Status = string.Empty;
        }
        finally { IsBusy = false; }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave) return;
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
                DateOnly.FromDateTime(StartDate!.Value));
            var result = await handler.Handle(command, cancellationToken);
            if (result.IsFailure)
            {
                Error = result.Error.Errors?.FirstOrDefault()?.Message ?? result.Error.Message;
                return;
            }
            IsSaved = true;
            Status = "Первый чат сохранён. Загрузка заданий будет следующим этапом.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /*ignore*/}
        catch (Exception)
        {
            Error = "Не удалось сохранить чат в settings.json. Проверьте доступ к папке ChatPulse.";
        }
        finally { IsBusy = false; }
    }

    private void Changed([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
