using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class VerifySubscriptionTaskDialog : Window
{
    private readonly WorkspaceTaskKind _kind;
    private readonly InstagramBrowserService _mainBrowser;
    private readonly Func<WorkspaceFailedTaskReport, Task> _saveFailedReport;
    private readonly string _mainUserName;
    private readonly IReadOnlyList<string> _targets;
    private readonly string? _singleTarget;
    private CancellationTokenSource _cancellation = new();
    private bool _running;

    public InstagramFollowTaskVerification? VerifiedResult { get; private set; }
    public bool HideConfirmed { get; private set; }

    public VerifySubscriptionTaskDialog(
        WorkspaceTaskKind kind,
        InstagramBrowserService mainBrowser,
        string mainUserName,
        IReadOnlyList<string> targets,
        Func<WorkspaceFailedTaskReport, Task> saveFailedReport)
    {
        if (kind is not (WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(mainBrowser);
        ArgumentNullException.ThrowIfNull(targets);

        _kind = kind;
        _mainBrowser = mainBrowser;
        _saveFailedReport = saveFailedReport;
        _mainUserName = InstagramUserName.Normalize(mainUserName);
        _targets = targets.Select(InstagramUserName.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        InitializeComponent();
        DialogTitle.Text = kind == WorkspaceTaskKind.SUBSCRIBE ? "Проверить подписку" : "Проверить отписку";
        ResultLabel.Text = kind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка";

        if (_targets.Count == 1)
        {
            _singleTarget = _targets[0];
            TaskSummary.Text = kind == WorkspaceTaskKind.SUBSCRIBE
                ? $"Проверим подписку на @{_singleTarget} с основного аккаунта."
                : $"Проверим отсутствие подписки на @{_singleTarget} с основного аккаунта.";
        }
        else if (_targets.Count > 1 && kind == WorkspaceTaskKind.UNSUBSCRIBE)
        {
            // The user does not choose one participant in a mass-unsubscribe task:
            // EVERY profile has to be checked, one by one.
            TaskSummary.Text = $"Проверим отсутствие подписки на всех {_targets.Count} аккаунтах.";
        }
        else if (_targets.Count > 1)
        {
            TaskSummary.Text = "В задании несколько Instagram-профилей. Выберите нужную участницу.";
            TargetSelectionPanel.Visibility = Visibility.Visible;
            foreach (var target in _targets)
                TargetPicker.Items.Add(target);
        }
        else
        {
            TaskSummary.Text = "Не удалось определить Instagram-профиль участницы из задания.";
            CheckButton.IsEnabled = false;
            ShowError("Проверка недоступна: в задании не найдена ссылка на профиль участницы.");
        }

        Loaded += (_, _) =>
        {
            MaxHeight = Math.Max(270, SystemParameters.WorkArea.Height * 0.9);
            DialogScroll.MaxHeight = Math.Max(180, MaxHeight - 100);
        };
        Closed += (_, _) => _cancellation.Dispose();
    }

    private async void Check_OnClick(object sender, RoutedEventArgs args)
    {
        if (_running || VerifiedResult is not null) return;
        var selected = _singleTarget ?? TargetPicker.SelectedItem as string;
        IReadOnlyList<string> targets = _kind == WorkspaceTaskKind.UNSUBSCRIBE && _targets.Count > 1
            ? _targets : string.IsNullOrWhiteSpace(selected) ? [] : [selected!];
        if (targets.Count == 0)
        {
            ShowError(_targets.Count == 0
                ? "В задании нет ссылки на Instagram-профиль. Проверка не выполнена."
                : "Выберите участницу, подписку на которую нужно проверить.");
            return;
        }

        _running = true;
        ResultRow.Visibility = Visibility.Collapsed;
        ResultsItems.Visibility = Visibility.Collapsed;
        ResultsItems.ItemsSource = null;
        DialogTitle.Text = _kind == WorkspaceTaskKind.SUBSCRIBE ? "Проверить подписку" : "Проверить отписку";
        CheckButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        TargetPicker.IsEnabled = false;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = $"Проверяем {targets.Count} профилей через @{_mainUserName}…";
        try
        {
            var progress = new Progress<string>(message => ProgressText.Text = message);
            var result = await InstagramFollowTaskRules.VerifyTargetsAsync(
                _mainUserName, _kind, targets,
                (name, shouldFollow, token) => _mainBrowser.CheckOwnFollowingAsync(
                    _mainUserName, name, shouldFollow, token),
                progress, _cancellation.Token);
            SelectionPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultsItems.ItemsSource = result.Results.Select(item => new
            {
                Name = $"@{item.TargetUserName}",
                Status = item.Check.Status == InstagramTaskCheckStatus.CONFIRMED
                    ? "✓ Подтверждено" : "Не подтверждено"
            }).ToArray();
            ResultsItems.Visibility = Visibility.Visible;
            DialogTitle.Text = "Результаты проверки";
            if (result.CanHide)
            {
                VerifiedResult = result;
                CloseButton.Content = "Закрыть отчёт и скрыть задание";
                CloseButton.Background = new SolidColorBrush(Color.FromRgb(0x75, 0x45, 0xB9));
                CloseButton.Foreground = Brushes.White;
                Grid.SetColumnSpan(CloseButton, 3);
                CheckButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowError(string.Join("\n", result.Results
                    .Where(item => item.Check.Status != InstagramTaskCheckStatus.CONFIRMED)
                    .Select(item => $"@{item.TargetUserName}: {item.Check.Detail}")));
                await SaveFailedReportAsync(WorkspaceFailedTaskReport.FromFollow(result));
                CheckButton.Content = "Повторить";
                CheckButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            ShowError("Проверка остановлена. Задание не скрыто.");
        }
        catch (Exception exception)
        {
            var detail = $"Проверка не завершена: {exception.Message}";
            ShowError(detail);
            await SaveFailedReportAsync(WorkspaceFailedTaskReport.FromError(
                _kind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка",
                _mainUserName, string.Join(", ", targets),
                targets.Select(name => $"{(_kind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка")} @{name}"),
                detail));
        }
        finally
        {
            // Close Chrome once, AFTER processing all participants in this task.
            try { await _mainBrowser.CloseBrowserAsync(); }
            catch (Exception exception)
            {
                ShowError($"Не удалось закрыть Chrome после проверки: {exception.Message}");
            }
            _running = false;
            if (IsLoaded && VerifiedResult is null)
            {
                CheckButton.IsEnabled = true;
                TargetPicker.IsEnabled = true;
            }
            CloseButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task SaveFailedReportAsync(WorkspaceFailedTaskReport report)
    {
        try
        {
            await _saveFailedReport(report);
        }
        catch (Exception exception)
        {
            ShowError($"Не удалось сохранить отчёт о проверке: {exception.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void Close_OnClick(object sender, RoutedEventArgs args)
    {
        if (_running) return;
        HideConfirmed = VerifiedResult?.CanHide == true;
        Close();
    }
}
