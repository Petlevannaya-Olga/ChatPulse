using System.Globalization;
using System.Windows;
using ChatPulse.Desktop.Services;

namespace ChatPulse.Desktop.Views.Dialogs;

/// <summary>Read-only details of previously saved checks or a Telegram participant register.</summary>
public partial class SubscriptionInfoDialog : Window
{
    private static readonly CultureInfo RUSSIAN_CULTURE = CultureInfo.GetCultureInfo("ru-RU");

    public SubscriptionInfoDialog(InstagramBatchResultSummary? summary)
    {
        InitializeComponent();
        TitleText.Text = "Последняя успешная проверка";
        SubtitleText.Text = "Результаты сохранённой проверки Instagram";
        if (summary is null)
        {
            UnavailableText.Text = "Успешных проверок подписок пока нет.";
            UnavailableText.Visibility = Visibility.Visible;
            return;
        }

        CheckDetailsPanel.Visibility = Visibility.Visible;
        CheckDateText.Text = summary.FinishedAtUtc.ToLocalTime()
            .ToString("dd.MM.yyyy, HH:mm", RUSSIAN_CULTURE);
        CheckCountText.Text = summary.ProgressLabel;
        CheckDirectionsText.Text = summary.CheckedDirectionsLabel;
        FoundText.Text = summary.Found.ToString(CultureInfo.InvariantCulture);
        NotFoundText.Text = summary.NotFound.ToString(CultureInfo.InvariantCulture);
        ErrorsText.Text = summary.Errors.ToString(CultureInfo.InvariantCulture);
        if (summary.HasNotFoundBreakdown)
        {
            CheckBreakdownPanel.Visibility = Visibility.Visible;
            NotFoundFollowingText.Text = summary.NotFoundFollowingLabel;
            NotFoundFollowersText.Text = summary.NotFoundFollowersLabel;
        }
        else
            LegacyCheckBreakdownText.Visibility = Visibility.Visible;
    }

    public SubscriptionInfoDialog(ParticipantListSourceInfo? source,
        InstagramParticipantCategoryCounts? counts)
    {
        InitializeComponent();
        TitleText.Text = "Список участниц";
        SubtitleText.Text = "Сведения об актуальном списке Telegram";
        if (source is null)
        {
            UnavailableText.Text = "Список участниц ещё не загружен.";
            UnavailableText.Visibility = Visibility.Visible;
            return;
        }

        ListDetailsPanel.Visibility = Visibility.Visible;
        ListSourceText.Text = source.DateLabel;
        ListMessageIdText.Text = $"Сообщение №{source.MessageId}";
        if (source.LastSuccessfulSyncAtUtc is null)
        {
            LastSyncText.Text = "Время ранее не сохранялось";
            LegacySyncHintText.Visibility = Visibility.Visible;
        }
        else
            LastSyncText.Text = source.LastSuccessfulSyncLabel;
        ListTotalText.Text = counts is null
            ? "—"
            : counts.Total.ToString(CultureInfo.InvariantCulture);
        CategoryCountsPanel.Visibility = counts is null ? Visibility.Collapsed : Visibility.Visible;
        if (counts is null) return;
        AdminCountText.Text = counts.Administration.ToString(CultureInfo.InvariantCulture);
        VipCountText.Text = counts.Vip.ToString(CultureInfo.InvariantCulture);
        RegularCountText.Text = counts.Regular.ToString(CultureInfo.InvariantCulture);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs args) => Close();
}
