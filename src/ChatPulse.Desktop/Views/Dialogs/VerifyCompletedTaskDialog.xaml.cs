using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class VerifyCompletedTaskDialog : Window
{
    private readonly string _postUrl;
    private readonly string _ownAccount;
    private readonly InstagramBrowserService _browser;
    private readonly Func<WorkspaceFailedTaskReport, Task> _saveFailedReport;
    private CancellationTokenSource _cancellation = new();
    private bool _running;

    public InstagramTaskVerificationResult? VerifiedResult { get; private set; }

    public VerifyCompletedTaskDialog(InstagramBrowserService browser, string postUrl, string ownAccount, string taskText,
        Func<WorkspaceFailedTaskReport, Task> saveFailedReport)
    {
        _browser = browser;
        _saveFailedReport = saveFailedReport;
        _postUrl = postUrl;
        _ownAccount = ownAccount;
        InitializeComponent();
        var suggested = InstagramTaskVerificationRules.SuggestChecks(taskText);
        // The like is mandatory and must not be deselected.
        CheckLikeBox.IsChecked = true;
        CheckCommentBox.IsChecked = suggested.Comment;
        CheckSaveBox.IsChecked = suggested.Save;
        CheckRepostBox.IsChecked = suggested.Repost;
        Loaded += (_, _) => UpdateWindowHeightLimits();
        Closed += (_, _) => _cancellation.Dispose();
    }


    private void UpdateWindowHeightLimits()
    {
        // SizeToContent tracks the compact result list, while long diagnostics stay scrollable.
        var availableHeight = SystemParameters.WorkArea.Height;
        MaxHeight = Math.Max(260, availableHeight * 0.9);
        DialogScroll.MaxHeight = Math.Max(190, MaxHeight - 100);
    }

    private void ShowNotice(string message)
    {
        ErrorsPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = message;
    }

    private void ShowResults(InstagramTaskVerificationResult result)
    {
        DialogTitle.Text = "Результаты проверки";
        ProgressPanel.Visibility = Visibility.Collapsed;

        var errors = new List<string>();
        SetCheckResult(CheckLikeBox, LikeLabel, LikeRow, LikeStatus, result.Like, "Лайк", errors);
        SetCheckResult(CheckCommentBox, CommentLabel, CommentRow, CommentStatus, result.Comment, "Комментарий", errors);
        SetCheckResult(CheckSaveBox, SaveLabel, SaveRow, SaveStatus, result.Save, "Сохранение", errors);
        SetCheckResult(CheckRepostBox, RepostLabel, RepostRow, RepostStatus, result.Repost, "Репост", errors);

        ErrorsText.Text = string.Join(Environment.NewLine, errors.Distinct(StringComparer.Ordinal));
        ErrorsPanel.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The only action after full confirmation closes the report and publishes the hide result.
        // Keep retry/close as separate actions when any selected check was not confirmed.
        SuccessHint.Visibility = Visibility.Collapsed;
        ConfigureResultButtons(result.CanHide);
    }

    private void ConfigureResultButtons(bool canHide)
    {
        if (canHide)
        {
            StartButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Закрыть отчёт и скрыть задание";
            CancelButton.Background = new SolidColorBrush(Color.FromRgb(0x75, 0x45, 0xB9));
            CancelButton.Foreground = Brushes.White;
            Grid.SetColumnSpan(CancelButton, 3);
            return;
        }

        StartButton.Visibility = Visibility.Visible;
        StartButton.Content = "Повторить";
        CancelButton.Content = "Закрыть";
        CancelButton.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xEB, 0xFA));
        CancelButton.Foreground = new SolidColorBrush(Color.FromRgb(0x39, 0x23, 0x5C));
        Grid.SetColumnSpan(CancelButton, 1);
    }

    private static void SetCheckResult(System.Windows.Controls.CheckBox checkBox,
        System.Windows.Controls.TextBlock itemLabel, FrameworkElement row, System.Windows.Controls.TextBlock status,
        InstagramTaskCheckResult? result, string label, List<string> errors)
    {
        var requested = checkBox.IsChecked == true;
        row.Visibility = requested ? Visibility.Visible : Visibility.Collapsed;
        if (!requested) return;
        checkBox.Visibility = Visibility.Collapsed;
        itemLabel.Visibility = Visibility.Visible;
        status.Visibility = Visibility.Visible;
        if (result?.Status == InstagramTaskCheckStatus.CONFIRMED)
        {
            status.Text = "✓ Подтверждено";
            return;
        }
        status.Text = "Не подтверждено";
        errors.Add($"{label}: {result?.Detail ?? "Проверка не завершена."}");
    }

    private async void Start_OnClick(object sender, RoutedEventArgs args)
    {
        if (_running) return;
        // Always include the mandatory like, regardless of optional selections.
        CheckLikeBox.IsChecked = true;
        string account;
        try { account = InstagramUserName.Normalize(_ownAccount); }
        catch (ArgumentException exception)
        {
            ShowNotice(exception.Message);
            return;
        }
        if (_cancellation.IsCancellationRequested)
        {
            _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
        }
        RestoreSelectionView();
        _running = true;
        ShowNotice("Проверяем выбранные действия…");
        StartButton.IsEnabled = false;
        CancelButton.Content = "Остановить";
        CheckLikeBox.IsEnabled = false;
        CheckCommentBox.IsEnabled = false;
        CheckSaveBox.IsEnabled = false;
        CheckRepostBox.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(stage => ProgressText.Text = stage);
            var result = await _browser.CheckTaskActivityAsync(_postUrl, account,
                true, CheckCommentBox.IsChecked == true,
                progress, _cancellation.Token, null,
                CheckSaveBox.IsChecked == true, CheckRepostBox.IsChecked == true);
            ShowResults(result);
            if (!result.CanHide)
                await SaveFailedReportAsync(WorkspaceFailedTaskReport.FromPost(result));
            if (result.CanHide)
            {
                VerifiedResult = result;
                StartButton.IsEnabled = false;
                return;
            }
        }
        catch (OperationCanceledException) { ShowNotice("Проверка остановлена."); }
        catch (Exception exception)
        {
            var detail = $"Проверка не завершена: {exception.Message}";
            ShowNotice(detail);
            await SaveFailedReportAsync(WorkspaceFailedTaskReport.FromError("Публикация", account,
                _postUrl, SelectedChecks(), detail));
        }
        finally
        {
            // The result is already collected. Close only the ChromeDriver-owned window;
            // keep its user-data directory so Instagram stays signed in for the next check.
            // This also runs after an error or cancellation, before the report is left open.
            try { await _browser.CloseBrowserAsync(); }
            catch (Exception exception) { AppendBrowserCloseError(exception); }
            _running = false;
            if (IsLoaded)
            {
                StartButton.IsEnabled = VerifiedResult is null;
                CancelButton.IsEnabled = true;
                CancelButton.Content = VerifiedResult is not null
                    ? "Закрыть отчёт и скрыть задание"
                    : "Закрыть";
                CheckLikeBox.IsEnabled = VerifiedResult is null;
                CheckCommentBox.IsEnabled = VerifiedResult is null;
                CheckSaveBox.IsEnabled = VerifiedResult is null;
                CheckRepostBox.IsEnabled = VerifiedResult is null;
            }
        }
    }

    private void AppendBrowserCloseError(Exception exception)
    {
        var detail = $"Не удалось закрыть Chrome после проверки: {exception.Message}";
        ErrorsText.Text = string.IsNullOrWhiteSpace(ErrorsText.Text)
            ? detail
            : ErrorsText.Text + Environment.NewLine + detail;
        ErrorsPanel.Visibility = Visibility.Visible;
    }

    private IEnumerable<string> SelectedChecks()
    {
        yield return "Лайк";
        if (CheckCommentBox.IsChecked == true) yield return "Комментарий";
        if (CheckSaveBox.IsChecked == true) yield return "Сохранение";
        if (CheckRepostBox.IsChecked == true) yield return "Репост";
    }

    private async Task SaveFailedReportAsync(WorkspaceFailedTaskReport report)
    {
        try
        {
            await _saveFailedReport(report);
        }
        catch (Exception exception)
        {
            var message = $"Не удалось сохранить отчёт о проверке: {exception.Message}";
            if (ErrorsPanel.Visibility == Visibility.Visible)
                ErrorsText.Text += Environment.NewLine + message;
            else
                ShowNotice(message);
        }
    }

    private void RestoreSelectionView()
    {
        DialogTitle.Text = "Проверить выполненное задание";
        ErrorsPanel.Visibility = Visibility.Collapsed;
        SuccessHint.Visibility = Visibility.Collapsed;
        ConfigureResultButtons(false);
        StartButton.Content = "Проверить";
        CheckLikeBox.IsChecked = true;
        RestoreCheckRow(LikeRow, CheckLikeBox, LikeLabel, LikeStatus);
        RestoreCheckRow(CommentRow, CheckCommentBox, CommentLabel, CommentStatus);
        RestoreCheckRow(SaveRow, CheckSaveBox, SaveLabel, SaveStatus);
        RestoreCheckRow(RepostRow, CheckRepostBox, RepostLabel, RepostStatus);
    }

    private static void RestoreCheckRow(FrameworkElement row, System.Windows.Controls.CheckBox checkBox,
        System.Windows.Controls.TextBlock itemLabel, System.Windows.Controls.TextBlock status)
    {
        row.Visibility = Visibility.Visible;
        checkBox.Visibility = Visibility.Visible;
        itemLabel.Visibility = Visibility.Collapsed;
        status.Visibility = Visibility.Collapsed;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs args)
    {
        if (_running)
        {
            _cancellation.Cancel();
            CancelButton.IsEnabled = false;
            ShowNotice("Останавливаем проверку…");
            return;
        }
        DialogResult = VerifiedResult is not null;
    }
}
