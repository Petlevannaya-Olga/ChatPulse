using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.Views.Workspace;

namespace ChatPulse.Desktop.Views.Dialogs;

/// <summary>Sequential manual task execution. Closing never saves the progress cursor.</summary>
public partial class WorkspaceTaskExecutionDialog : Window
{
    private readonly WorkspaceTaskExecutionSession _session;
    private bool _busy = true;
    private bool _stopRequested;

    public WorkspaceTaskExecutionDialog(WorkspaceTaskReader reader,
        ChatSynchronizationService synchronization, ConfiguredChat chat, bool oldestFirst)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(synchronization);
        ArgumentNullException.ThrowIfNull(chat);
        _session = new WorkspaceTaskExecutionSession(reader, synchronization, chat, oldestFirst);
        InitializeComponent();
        ChatTitle.Text = chat.DisplayName.Length > 0 ? chat.DisplayName : chat.TelegramTitle;
        Loaded += Window_OnLoaded;
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= Window_OnLoaded;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height * 0.92);
        Height = Math.Min(670, MaxHeight);
        try
        {
            var task = await _session.StartAsync();
            if (_stopRequested)
            {
                _busy = false;
                Close();
                return;
            }
            DisplayTask(task);
            StatusText.Text = task is null
                ? "Невыполненных заданий нет."
                : "Выполните задание вручную и нажмите «Готово».";
        }
        catch (Exception exception)
        {
            ShowError($"Не удалось загрузить задания: {exception.Message}");
            StatusText.Text = "Остановите выполнение и попробуйте снова.";
        }
        finally
        {
            _busy = false;
            StopButton.IsEnabled = true;
            CompleteButton.IsEnabled = !_stopRequested && _session.Current is not null;
        }
    }

    private async void CompleteButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_busy || _session.Current is null) return;
        _busy = true;
        CompleteButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Text = "Ставим ❤️ исходному сообщению Telegram…";
        try
        {
            var next = await _session.CompleteCurrentAsync();
            if (_stopRequested)
            {
                _busy = false;
                Close();
                return;
            }
            await FlipToTaskAsync(next);
            if (_stopRequested)
            {
                _busy = false;
                Close();
                return;
            }
            StatusText.Text = next is null
                ? "Все задания текущего запуска выполнены."
                : "Задание отмечено в Telegram. Продолжайте со следующим.";
        }
        catch (Exception exception)
        {
            // Do not advance or count a task if Telegram/SQLite did not confirm completion.
            ShowError($"Не удалось отметить задание: {exception.Message}");
            StatusText.Text = "Проверьте статус реакции, затем повторите попытку.";
        }
        finally
        {
            _busy = false;
            StopButton.IsEnabled = true;
            CompleteButton.IsEnabled = !_stopRequested && _session.Current is not null;
            if (_session.Current is null) StopButton.Content = "Закрыть";
        }
    }

    // Turn the whole task surface (including its frame and links), not just its text.
    // The next task is substituted only when the old card is edge-on.
    private async Task FlipToTaskAsync(WorkspaceDetectedTask? next)
    {
        const double EDGE_WIDTH = 0.035;
        FlippingCard.IsHitTestVisible = false;
        try
        {
            await AnimateCardHalfAsync(1, EDGE_WIDTH, 1, 0.97,
                0, -5, 0, 7, 230, EasingMode.EaseIn);

            DisplayTask(next);
            FlippingCard.UpdateLayout();

            await AnimateCardHalfAsync(EDGE_WIDTH, 1, 0.97, 1,
                5, 0, -7, 0, 245, EasingMode.EaseOut);
        }
        finally
        {
            // Do not leave the card edge-on if a transition is interrupted.
            FlipScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            FlipScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            FlipSkew.BeginAnimation(System.Windows.Media.SkewTransform.AngleYProperty, null);
            FlipOffset.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            FlipScale.ScaleX = 1;
            FlipScale.ScaleY = 1;
            FlipSkew.AngleY = 0;
            FlipOffset.X = 0;
            FlippingCard.IsHitTestVisible = true;
        }
    }

    private Task AnimateCardHalfAsync(
        double fromScaleX, double toScaleX,
        double fromScaleY, double toScaleY,
        double fromSkewY, double toSkewY,
        double fromOffsetX, double toOffsetX,
        int durationMilliseconds, EasingMode easingMode)
    {
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);

        DoubleAnimation MakeAnimation(double from, double to) => new(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = easingMode },
            FillBehavior = FillBehavior.Stop
        };

        // Base values equal the final values so the finished animation cannot snap back.
        FlipScale.ScaleX = toScaleX;
        FlipScale.ScaleY = toScaleY;
        FlipSkew.AngleY = toSkewY;
        FlipOffset.X = toOffsetX;

        var widthAnimation = MakeAnimation(fromScaleX, toScaleX);
        widthAnimation.Completed += (_, _) => finished.TrySetResult(true);
        FlipScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            MakeAnimation(fromScaleY, toScaleY));
        FlipSkew.BeginAnimation(System.Windows.Media.SkewTransform.AngleYProperty,
            MakeAnimation(fromSkewY, toSkewY));
        FlipOffset.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            MakeAnimation(fromOffsetX, toOffsetX));
        FlipScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, widthAnimation);

        return finished.Task;
    }

    private void DisplayTask(WorkspaceDetectedTask? task)
    {
        var progress = _session.Progress;
        ExecutionProgress.Maximum = Math.Max(1, progress.Total);
        ExecutionProgress.Value = progress.Total == 0 ? 1 : progress.Completed;
        CountText.Text = $"{progress.Completed} / {progress.Total}";
        RemainingText.Text = $"Осталось: {progress.Remaining}";
        StepText.Text = task is null
            ? "Выполнение завершено"
            : $"Задание {progress.CurrentNumber} из {progress.Total}";
        TaskContent.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
        FinishedPanel.Visibility = task is null ? Visibility.Visible : Visibility.Collapsed;

        if (task is null)
        {
            FinishedText.Text = progress.Total == 0
                ? "В этом чате сейчас нет невыполненных заданий."
                : $"В этом запуске выполнено {progress.Completed} заданий.";
            CompleteButton.Visibility = Visibility.Collapsed;
            StopButton.Content = "Закрыть";
            return;
        }

        var card = new WorkspaceTaskCard(task);
        TaskKindText.Text = card.KindLabel;
        TaskTimeText.Text = card.TimeLabel.Length > 0
            ? $"{card.DateGroupLabel} · {card.TimeLabel}" : card.DateGroupLabel;
        TaskBody.Text = card.Description;
        TaskTextScrollViewer.ScrollToTop();
        var links = card.ActionLinks
            .Where(link => link.Url is { } url &&
                Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.Host is ("t.me" or "instagram.com" or "www.instagram.com") &&
                !string.Equals(link.Url, card.SourceUrl, StringComparison.OrdinalIgnoreCase))
            .Select(link => new WorkspaceTaskTextPart(link.Text switch
            {
                "Открыть Reel" => "Reel",
                "Открыть публикацию" => "Публикация",
                "Открыть Instagram" => "Instagram",
                "Открыть Telegram" => "Пост",
                "Открыть ссылку" => "Ссылка",
                _ => link.Text
            }, link.Url))
            .ToList();
        if (card.HasSourceUrl && !links.Any(link =>
                string.Equals(link.Url, card.SourceUrl, StringComparison.OrdinalIgnoreCase)))
        {
            links.Add(new WorkspaceTaskTextPart("Задание", card.SourceUrl));
        }
        TaskLinks.ItemsSource = links;
        LinksPanel.Visibility = links.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompleteButton.Visibility = Visibility.Visible;
        StopButton.Content = "Остановить";
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs args)
    {
        _stopRequested = true;
        if (!_busy)
        {
            Close();
            return;
        }
        StopButton.IsEnabled = false;
        StatusText.Text = "Остановим после завершения текущей операции Telegram.";
    }

    private void Dialog_OnClosing(object sender, CancelEventArgs args)
    {
        if (!_busy) return;
        args.Cancel = true;
        _stopRequested = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Дожидаемся завершения текущей операции Telegram…";
    }

    private void OpenTaskLink_OnClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string link } ||
            !Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("instagram.com" or "www.instagram.com" or "t.me")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError($"Не удалось открыть ссылку: {exception.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
