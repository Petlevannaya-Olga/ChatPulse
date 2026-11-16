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
        if (!IsVisible || ViewModel is not { } viewModel) return;
        await viewModel.EnsureLoadedAsync();
        SyncRows(viewModel.Chats);
    }

    private void SyncRows(IEnumerable<TelegramChatOption> chats)
    {
        var selectedKeys = Rows.Where(row => row.IsSelected)
            .Select(row => (row.Chat.PeerId, row.Chat.PeerType)).ToHashSet();
        updating = true;
        Rows.Clear();
        foreach (var chat in chats)
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

    private void ShowReview_OnClick(object sender, RoutedEventArgs e)
    {
        if (!HasSelection) return;
        isReview = true;
        Changed(nameof(SelectionVisibility));
        Changed(nameof(ReviewVisibility));
    }

    private void ReturnToSelection_OnClick(object sender, RoutedEventArgs e)
    {
        isReview = false;
        Changed(nameof(SelectionVisibility));
        Changed(nameof(ReviewVisibility));
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
