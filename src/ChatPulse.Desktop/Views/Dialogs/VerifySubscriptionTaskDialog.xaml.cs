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
    private readonly string? _singleTarget;
    private CancellationTokenSource _cancellation = new();
    private bool _running;

    public InstagramFollowTaskVerification? VerifiedResult { get; private set; }

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
        InitializeComponent();
        DialogTitle.Text = kind == WorkspaceTaskKind.SUBSCRIBE ? "Проверить подписку" : "Проверить отписку";
        ResultLabel.Text = kind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка";

        if (targets.Count == 1)
        {
            _singleTarget = InstagramUserName.Normalize(targets[0]);
            TaskSummary.Text = kind == WorkspaceTaskKind.SUBSCRIBE
                ? $"Проверим подписку на @{_singleTarget} с основного аккаунта."
                : $"Проверим отсутствие подписки на @{_singleTarget} с основного аккаунта.";
        }
        else if (targets.Count > 1)
        {
            TaskSummary.Text = "В задании несколько Instagram-профилей. Выберите нужную участницу.";
            TargetSelectionPanel.Visibility = Visibility.Visible;
            foreach (var target in targets)
                TargetPicker.Items.Add(InstagramUserName.Normalize(target));
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
        var target = _singleTarget ?? TargetPicker.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(target))
        {
            ShowError(TargetPicker.Items.Count == 0
                ? "В задании нет ссылки на Instagram-профиль участницы. Проверка не выполнена."
                : "Выберите участницу, подписку на которую нужно проверить.");
            return;
        }
        _running = true;
        ResultRow.Visibility = Visibility.Collapsed;
        DialogTitle.Text = _kind == WorkspaceTaskKind.SUBSCRIBE ? "Проверить подписку" : "Проверить отписку";
        CheckButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        TargetPicker.IsEnabled = false;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = $"Проверяем @{target} через основной аккаунт @{_mainUserName}…";
        try
        {
            var result = await _mainBrowser.CheckOwnFollowingAsync(
                _mainUserName, target, _kind == WorkspaceTaskKind.SUBSCRIBE, _cancellation.Token);
            SelectionPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultRow.Visibility = Visibility.Visible;
            DialogTitle.Text = "Результаты проверки";
            ResultLabel.Text = $"{(_kind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка")} @{target}";
            if (result.Status == InstagramTaskCheckStatus.CONFIRMED)
            {
                VerifiedResult = new InstagramFollowTaskVerification(_mainUserName, target, _kind, result);
                ResultStatus.Text = "✓ Подтверждено";
                CloseButton.Content = "Закрыть отчёт и скрыть задание";
                CloseButton.Background = new SolidColorBrush(Color.FromRgb(0x75, 0x45, 0xB9));
                CloseButton.Foreground = Brushes.White;
                Grid.SetColumnSpan(CloseButton, 3);
                CheckButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ResultStatus.Text = "Не подтверждено";
                ShowError(result.Detail);
                await SaveFailedReportAsync(WorkspaceFailedTaskReport.FromFollow(
                    new InstagramFollowTaskVerification(_mainUserName, target, _kind, result)));
                CheckButton.Content = "Повторить";
                CheckButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            ShowError("Проверка остановлена.");
        }
        catch (Exception exception)
        {
            var detail = $"Проверка не завершена: {exception.Message}";
            ShowError(detail);
            await SaveFailedReportAsync(WorkspaceFailedTaskReport.FromError(
                _kind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка",
                _mainUserName, target,
                [_kind == WorkspaceTaskKind.SUBSCRIBE ? "Подписка" : "Отписка"], detail));
        }
        finally
        {
            _running = false;
            if (IsLoaded && VerifiedResult is null)
            {
                CheckButton.IsEnabled = true;
                TargetPicker.IsEnabled = true;
                SelectionPanel.Visibility = Visibility.Visible;
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
        DialogResult = VerifiedResult?.CanHide == true;
    }
}
