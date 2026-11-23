using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ChatPulse.Desktop.Services;

namespace ChatPulse.Desktop.Views.Dialogs;

public partial class FailedTaskVerificationReportsDialog : Window
{
    private static readonly CultureInfo RUSSIAN_CULTURE = CultureInfo.GetCultureInfo("ru-RU");

    public FailedTaskVerificationReportsDialog(IReadOnlyList<WorkspaceFailedTaskReport> reports)
    {
        InitializeComponent();
        ReportItems.ItemsSource = reports.Select(report => new ReportView(
            $"{report.CheckedAtUtc.ToLocalTime().ToString("dd MMMM yyyy, HH:mm", RUSSIAN_CULTURE)} · {report.Kind}",
            $"@{report.ViewerUserName} · {report.Target}",
            report.Checks.Select(check => new CheckView(
                check.Name,
                check.Confirmed ? "✓ Подтверждено" : "Не подтверждено",
                check.Confirmed ? Brushes.ForestGreen : new SolidColorBrush(Color.FromRgb(0xB1, 0x49, 0x65)),
                check.Confirmed ? string.Empty : check.Detail)).ToArray())).ToArray();
        Loaded += (_, _) =>
        {
            MaxHeight = Math.Max(340, SystemParameters.WorkArea.Height * 0.9);
            ReportScroll.MaxHeight = Math.Max(100, MaxHeight - 240);
        };
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private sealed record ReportView(string Heading, string Subtitle, IReadOnlyList<CheckView> Checks);
    private sealed record CheckView(string Name, string Status, Brush StatusBrush, string Detail);
}
