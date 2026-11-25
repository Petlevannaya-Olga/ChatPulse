using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class VerifySubscriptionTaskDialog : Window
{
    private readonly InstagramBrowserService _mainBrowser;
    private readonly Func<WorkspaceFailedTaskReport, Task> _saveFailedReport;
    private readonly string _mainUserName;
    private readonly IReadOnlyList<WorkspaceFollowAction> _actions;
    private CancellationTokenSource _cancellation = new();
    private bool _running;

    public InstagramFollowTaskVerification? VerifiedResult { get; private set; }
    public bool HideConfirmed { get; private set; }

    public VerifySubscriptionTaskDialog(
        InstagramBrowserService mainBrowser,
        string mainUserName,
        IReadOnlyList<WorkspaceFollowAction> actions,
        Func<WorkspaceFailedTaskReport, Task> saveFailedReport)
    {
        ArgumentNullException.ThrowIfNull(mainBrowser);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(saveFailedReport);
        _mainBrowser = mainBrowser;
        _saveFailedReport = saveFailedReport;
        _mainUserName = InstagramUserName.Normalize(mainUserName);
        _actions = actions;
        InitializeComponent();
        var mixed = _actions.Any(action => action.Kind != _actions[0].Kind);
        var label = mixed ? "Подписка и отписка"
            : _actions[0].ShouldFollow ? "Подписка" : "Отписка";
        DialogTitle.Text = "Проверить: " + label.ToLowerInvariant();
        ResultLabel.Text = label;
        TaskSummary.Text = _actions.Count == 1
            ? $"Проверим {(_actions[0].ShouldFollow ? "подписку на" : "отсутствие подписки на")} " +
              $"@{_actions[0].TargetUserName} с основного аккаунта."
            : $"Проверим все {_actions.Count} действия в этом Telegram-сообщении " +
              "(подписки и отписки по порядку).";

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
        _running = true;
        ResultRow.Visibility = Visibility.Collapsed;
        ResultsItems.Visibility = Visibility.Collapsed;
        ResultsItems.ItemsSource = null;
        CheckButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = $"Проверяем {_actions.Count} действий через @{_mainUserName}…";
        try
        {
            var progress = new Progress<string>(message => ProgressText.Text = message);
            var result = await InstagramFollowTaskRules.VerifyActionsAsync(
                _mainUserName, _actions,
                (name, shouldFollow, token) => _mainBrowser.CheckOwnFollowingAsync(
                    _mainUserName, name, shouldFollow, token, stopOnTechnicalFailure: true),
                progress, _cancellation.Token, stopOnTechnicalFailure: true);
            SelectionPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultsItems.ItemsSource = result.Results.Select(item => new
            {
                Name = $"{(item.ActionKind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка")} @{item.TargetUserName}",
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
                "Подписка и отписка", _mainUserName,
                string.Join(", ", _actions.Select(action => $"@{action.TargetUserName}")),
                _actions.Select(action =>
                    $"{(action.ShouldFollow ? "Подписка" : "Отписка")} @{action.TargetUserName}"),
                detail, technicalFailure: true));
        }
        finally
        {
            try { await _mainBrowser.CloseBrowserAsync(); }
            catch (Exception exception)
            {
                ShowError($"Не удалось закрыть Chrome после проверки: {exception.Message}");
            }
            _running = false;
            if (IsLoaded && VerifiedResult is null)
                CheckButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task SaveFailedReportAsync(WorkspaceFailedTaskReport report)
    {
        try { await _saveFailedReport(report); }
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
