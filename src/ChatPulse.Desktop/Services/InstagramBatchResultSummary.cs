using System.Globalization;

namespace ChatPulse.Desktop.Services;

public enum InstagramBatchOutcome
{
    COMPLETED,
    STOPPED,
    FAILED
}

/// <summary>
/// Counts refer to Instagram searches, not unique participants. An absent search result
/// is not proof of an unfollow. Directional counts are nullable for legacy records.
/// </summary>
public sealed record InstagramBatchResultSummary(
    DateTimeOffset FinishedAtUtc,
    InstagramBatchOutcome Outcome,
    int CompletedSearches,
    int TotalSearches,
    int Found,
    int NotFound,
    int Errors,
    int PrivilegedParticipants,
    int MissingActivityProfiles,
    string Details = "",
    int? NotFoundFollowers = null,
    int? NotFoundFollowing = null,
    InstagramCheckDirections Directions = InstagramCheckDirections.BOTH)
{
    public string OutcomeLabel => Outcome switch
    {
        InstagramBatchOutcome.COMPLETED => "Проверка завершена",
        InstagramBatchOutcome.STOPPED => "Проверка остановлена",
        _ => "Проверка прервана ошибкой"
    };

    public string OutcomeColor => Outcome switch
    {
        InstagramBatchOutcome.COMPLETED => "#248357",
        InstagramBatchOutcome.STOPPED => "#8243C7",
        _ => "#B04B47"
    };

    public string CheckedDirectionsLabel => Directions switch
    {
        InstagramCheckDirections.FOLLOWERS => "Проверены только подписки участниц на меня",
        InstagramCheckDirections.FOLLOWING => "Проверены только мои подписки на участниц",
        _ => "Проверены мои подписки на участниц и подписки участниц на меня"
    };
    public string ProgressLabel
    {
        get
        {
            var lastTwoDigits = CompletedSearches % 100;
            var lastDigit = CompletedSearches % 10;

            var (verb, noun) = lastTwoDigits is >= 11 and <= 14
                ? ("Выполнено", "проверок")
                : lastDigit switch
                {
                    1 => ("Выполнена", "проверка"),
                    2 or 3 or 4 => ("Выполнены", "проверки"),
                    _ => ("Выполнено", "проверок")
                };

            return $"{verb} {CompletedSearches} {noun} подписок";
        }
    }
    public string FinishedAtLabel => $"Последняя успешная проверка: {FinishedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))}";
    public bool HasNotFoundBreakdown => NotFoundFollowers.HasValue && NotFoundFollowing.HasValue;
    public bool HasLegacyBreakdown => !HasNotFoundBreakdown;
    public string NotFoundFollowersLabel => (Directions & InstagramCheckDirections.FOLLOWERS) == 0
        ? "—" : NotFoundFollowers?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string NotFoundFollowingLabel => (Directions & InstagramCheckDirections.FOLLOWING) == 0
        ? "—" : NotFoundFollowing?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public bool IsStopped => Outcome == InstagramBatchOutcome.STOPPED;
    public bool ShowDetailedResult => !IsStopped;
    public string StoppedNotice => "Результаты последней успешной проверки сохранены без изменений.";
    public bool IsInterrupted => Outcome != InstagramBatchOutcome.COMPLETED;
    public string InterruptedNotice => "Временные результаты удалены. Во вкладке «Подписки» остаётся последняя успешная проверка.";
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public string Caution => "Отсутствие в поиске Instagram не доказывает отписку.";
}
