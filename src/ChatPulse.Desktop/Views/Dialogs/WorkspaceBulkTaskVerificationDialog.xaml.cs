using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class WorkspaceBulkTaskVerificationDialog : Window
{
    private readonly WorkspaceBulkTaskVerifier _runner;
    private readonly ConfiguredChat _chat;
    private readonly string _viewer;
    private readonly bool _oldestFirst;
    private readonly ObservableCollection<LogEntry> _entries = [];
    private bool _running;
    private bool _stopRequested;
    private bool _emergencyStopped;
    private int _total;
    private int _processed;
    private int _failed;
    private int _skipped;
    private int _runHidden;
    private int _processedAcrossRuns;

    public int HiddenCount { get; private set; }
    public int ProcessedCount => _processedAcrossRuns;

    public WorkspaceBulkTaskVerificationDialog(WorkspaceBulkTaskVerifier runner,
        ConfiguredChat chat, string viewer, bool oldestFirst)
    {
        _runner = runner;
        _chat = chat;
        _viewer = InstagramUserName.Normalize(viewer);
        _oldestFirst = oldestFirst;
        InitializeComponent();
        LogItems.ItemsSource = _entries;
        Loaded += (_, _) =>
        {
            Height = Math.Min(720, Math.Max(460, SystemParameters.WorkArea.Height * 0.88));
            MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        };
    }

    private async void ActionButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_running)
        {
            RequestStop();
            return;
        }
        // A new attempt always uses a fresh SQLite snapshot. Reports stay in SQLite,
        // but progress and log belong to this attempt only.
        _stopRequested = false;
        _emergencyStopped = false;
        _total = 0;
        _processed = 0;
        _failed = 0;
        _skipped = 0;
        _runHidden = 0;
        _entries.Clear();
        UpdateCounters();
        var mode = CheckAllRadio.IsChecked == true
            ? WorkspaceBulkVerificationMode.CheckAll
            : WorkspaceBulkVerificationMode.ExcludePreviouslyChecked;
        _running = true;
        CloseButton.IsEnabled = false;
        ExcludePreviouslyCheckedRadio.IsEnabled = false;
        CheckAllRadio.IsEnabled = false;
        ActionButton.Content = "Остановить после текущего";
        FinalMessage.Text = "Проверяем задания последовательно. Сначала завершим текущее задание.";
        try
        {
            CurrentTaskText.Text = "Составляем список выполненных заданий…";
            StageText.Text = "Чтение списка из SQLite";
            var ids = await _runner.GetSnapshotAsync(_chat, _oldestFirst, () => _stopRequested, mode);
            _total = ids.Count;
            var excludedPosts = _runner.ExcludedFailedPostCount;
            var excludedFollows = _runner.ExcludedFailedFollowCount;
            UpdateCounters();
            if (excludedPosts + excludedFollows > 0)
                AppendEntry(new LogEntry("Ранее не подтверждённые задания", "Исключено",
                    $"Публикаций: {excludedPosts}; подписок/отписок: {excludedFollows}. " +
                    "Повторная проверка — вручную. Технические сбои не исключают задания.",
                    Brushes.DimGray));
            for (var index = 0; index < ids.Count; index++)
            {
                if (!WorkspaceBulkTaskVerificationPolicy.ShouldContinue(_stopRequested)) break;
                var id = ids[index];
                CurrentTaskText.Text = $"Задание {index + 1} из {_total} · Telegram #{id}";
                StageText.Text = "Подготовка проверки Instagram…";
                var stage = new Progress<string>(value => StageText.Text = value);
                WorkspaceBulkTaskOutcome outcome;
                try
                {
                    outcome = await _runner.VerifyOneAsync(_chat, id, _viewer, stage, mode);
                }
                catch (Exception exception)
                {
                    _emergencyStopped = true;
                    CurrentTaskText.Text = $"Остановлено из-за технической ошибки · Telegram #{id}";
                    StageText.Text = "Техническая ошибка. Очередь остановлена.";
                    AppendEntry(new LogEntry($"Задание #{id}", "Техническая ошибка — остановлено",
                        exception.Message, new SolidColorBrush(Color.FromRgb(0xA9, 0x46, 0x69))));
                    FinalMessage.Text = "Проверка прервана из-за технической ошибки. " +
                        "Проверка текущего задания не завершена, следующие задания не запускались. " +
                        $"Обработано до сбоя: {_processed} из {_total}. " +
                        "Ранее сохранённые результаты остаются без изменений. Chrome будет закрыт.";
                    break;
                }
                _processed++;
                _processedAcrossRuns++;
                if (outcome.Hidden)
                {
                    HiddenCount++;
                    _runHidden++;
                }
                else if (outcome.Skipped) _skipped++;
                else _failed++;
                AppendEntry(new LogEntry($"Задание #{id}",
                    outcome.Hidden ? "✓ Скрыто" : outcome.Skipped ? "Пропущено" : "Не подтверждено",
                    outcome.Detail,
                    outcome.Hidden ? Brushes.ForestGreen :
                        new SolidColorBrush(Color.FromRgb(0xA9, 0x46, 0x69))));
                UpdateCounters();
            }
            if (!_emergencyStopped)
            {
                CurrentTaskText.Text = _stopRequested ? "Проверка остановлена" : "Проверка завершена";
                StageText.Text = _stopRequested
                    ? "Новое задание не запущено; результат последнего обработанного сохранён."
                    : "Все задания из начального списка обработаны.";
                FinalMessage.Text = $"Обработано {_processed} из {_total}. Скрыто {_runHidden}, " +
                    $"с расхождениями {_failed}, пропущено {_skipped}.";
                if (mode == WorkspaceBulkVerificationMode.ExcludePreviouslyChecked)
                    FinalMessage.Text += $" Исключено ранее неподтверждённых: " +
                        $"публикации {excludedPosts}, подписки и отписки {excludedFollows}.";
            }
        }
        catch (Exception exception)
        {
            StageText.Text = "Ошибка подготовки массовой проверки";
            AppendEntry(new LogEntry("Ошибка", "Не завершено", exception.Message,
                new SolidColorBrush(Color.FromRgb(0xA9, 0x46, 0x69))));
            FinalMessage.Text = "Обработка остановлена. Уже сохранённые результаты не отменяются.";
        }
        finally
        {
            // Keep one Chrome session for the whole batch. A soft stop first
            // completes the current task and its SQLite write; only then quit Chrome.
            StageText.Text = "Закрываем Chrome…";
            var browserClosed = false;
            try
            {
                await _runner.CloseBrowserAsync();
                browserClosed = true;
            }
            catch (Exception exception)
            {
                AppendEntry(new LogEntry("Chrome", "Ошибка закрытия",
                    exception.Message, new SolidColorBrush(Color.FromRgb(0xA9, 0x46, 0x69))));
                FinalMessage.Text += $" Не удалось закрыть Chrome: {exception.Message}";
            }
            finally
            {
                StageText.Text = !browserClosed ? "Chrome не удалось закрыть автоматически."
                    : _emergencyStopped ? "Проверка остановлена из-за технической ошибки. Chrome закрыт."
                    : _stopRequested ? "Проверка остановлена. Chrome закрыт."
                    : "Проверка завершена. Chrome закрыт.";
                _running = false;
                CloseButton.IsEnabled = true;
                ExcludePreviouslyCheckedRadio.IsEnabled = true;
                CheckAllRadio.IsEnabled = true;
                ActionButton.IsEnabled = true;
                ActionButton.Content = "Проверить повторно";
            }
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (!_running) Close();
    }

    private void UpdateCounters()
    {
        CountText.Text = $"{_processed} / {_total}";
        BatchProgressBar.Maximum = Math.Max(1, _total);
        BatchProgressBar.Value = _processed;
        TotalsText.Text = $"Скрыто: {_runHidden} · С ошибками: {_failed} · Пропущено: {_skipped}";
    }

    private void AppendEntry(LogEntry entry)
    {
        // Keep the progress window bounded even for chats with many thousands of tasks.
        if (_entries.Count >= 250) _entries.RemoveAt(0);
        _entries.Add(entry);
        LogScroll.ScrollToEnd();
    }

    private void RequestStop()
    {
        _stopRequested = true;
        ActionButton.Content = "Ожидаем завершения…";
        ActionButton.IsEnabled = false;
        FinalMessage.Text = "Остановка запрошена. Текущее задание будет завершено и сохранено.";
    }

    private void Dialog_OnClosing(object sender, CancelEventArgs args)
    {
        if (!_running) return;
        args.Cancel = true;
        RequestStop();
    }

    private sealed record LogEntry(string Title, string Status, string Detail, Brush StatusBrush);
}
