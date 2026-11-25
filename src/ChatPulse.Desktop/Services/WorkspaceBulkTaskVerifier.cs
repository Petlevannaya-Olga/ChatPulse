using ChatPulse.Desktop.Settings;
using OpenQA.Selenium;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Runs a stable snapshot of completed messages one by one. A soft stop never interrupts
/// an Instagram check or a SQLite transaction already in progress.
/// </summary>
public sealed class WorkspaceBulkTaskVerifier(
    WorkspaceTaskReader reader,
    InstagramBrowserService browser)
{
    // A single service instance is shared by every task in the batch.
    // Chrome is closed only after the batch finishes or a soft stop completes.
    public Task CloseBrowserAsync() => browser.CloseBrowserAsync();

    public int ExcludedFailedPostCount { get; private set; }
    public int ExcludedFailedFollowCount { get; private set; }

    public async Task<IReadOnlyList<long>> GetSnapshotAsync(ConfiguredChat chat,
        bool oldestFirst, Func<bool> stopRequested,
        WorkspaceBulkVerificationMode mode = WorkspaceBulkVerificationMode.ExcludePreviouslyChecked)
    {
        const int PAGE_SIZE = 60;
        ExcludedFailedPostCount = 0;
        ExcludedFailedFollowCount = 0;
        var ids = new List<long>();
        var failedStore = new WorkspaceFailedTaskVerificationStore(reader.DatabasePath);
        for (var offset = 0; ; offset += PAGE_SIZE)
        {
            if (stopRequested()) break;
            var page = await reader.ReadPageAsync(chat, WorkspaceTaskState.Completed,
                oldestFirst, offset, PAGE_SIZE, includeTotals: false);
            // Filter *after* reading each full page. Offsets still refer to the
            // unfiltered completed-task stream, so later pages are never skipped.
            var manualIds = mode == WorkspaceBulkVerificationMode.ExcludePreviouslyChecked
                ? await failedStore.FindRequiringManualReviewAsync(chat,
                    page.Tasks.Select(task => task.MessageId).ToArray())
                : (IReadOnlySet<long>)new HashSet<long>();
            foreach (var task in page.Tasks)
            {
                if (!WorkspaceBulkTaskVerificationPolicy.ShouldExcludeFromBulk(
                        manualIds.Contains(task.MessageId), mode))
                {
                    ids.Add(task.MessageId);
                    continue;
                }
                var kind = WorkspaceTaskKindDetector.Detect(task.Text, task.PostUrl);
                if (kind is WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE or WorkspaceTaskKind.MIXED)
                    ExcludedFailedFollowCount++;
                else
                    ExcludedFailedPostCount++;
            }
            if (page.Tasks.Count < PAGE_SIZE) break;
        }
        return ids;
    }

    public async Task<WorkspaceBulkTaskOutcome> VerifyOneAsync(
        ConfiguredChat chat, long messageId, string viewer,
        IProgress<string>? stage = null,
        WorkspaceBulkVerificationMode mode = WorkspaceBulkVerificationMode.ExcludePreviouslyChecked)
    {
        var task = await reader.ReadCompletedByIdAsync(chat, messageId);
        if (task is null)
            return new WorkspaceBulkTaskOutcome(messageId, false,
                "Задание уже скрыто или больше не находится в разделе «Выполненные».", true);

        var failedStore = new WorkspaceFailedTaskVerificationStore(reader.DatabasePath);
        var kind = WorkspaceTaskKindDetector.Detect(task.Text, task.PostUrl);
        // Recheck immediately before opening Instagram only for the default
        // mode: explicit CheckAll permits rechecking earlier business failures.
        if (mode == WorkspaceBulkVerificationMode.ExcludePreviouslyChecked &&
            (await failedStore.FindRequiringManualReviewAsync(chat, [messageId])).Contains(messageId))
            return new WorkspaceBulkTaskOutcome(messageId, false,
                "У задания уже есть неподтверждённая проверка. Повторная проверка доступна вручную.", true);

        var kindLabel = kind switch
        {
            WorkspaceTaskKind.SUBSCRIBE => "Подписка",
            WorkspaceTaskKind.UNSUBSCRIBE => "Отписка",
            WorkspaceTaskKind.MIXED => "Подписка и отписка",
            WorkspaceTaskKind.STORY => "Сторис",
            _ => "Публикация"
        };
        string target = task.PostUrl;
        try
        {
            if (kind is WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE or WorkspaceTaskKind.MIXED)
            {
                var allActions = WorkspaceFollowActions.GetActions(task.Text, task.PostUrl);
                if (allActions.Count == 0)
                    return await FailedAsync("В задании нет Instagram-профилей для проверки.");
                var ownAccounts = await new InstagramOwnAccountStore(reader.DatabasePath).ReadAsync();
                var actions = WorkspaceFollowActions.WithoutOwnAccounts(allActions, ownAccounts, viewer);
                if (actions.Count == 0)
                    return new WorkspaceBulkTaskOutcome(messageId, false,
                        "Только собственные Instagram-аккаунты: проверка не требуется.", true);
                target = string.Join(", ", actions.Select(action => $"@{action.TargetUserName}"));
                var verification = await InstagramFollowTaskRules.VerifyActionsAsync(viewer, actions,
                    (name, shouldFollow, token) => browser.CheckOwnFollowingAsync(viewer, name, shouldFollow,
                        token, stopOnTechnicalFailure: true),
                    stage, CancellationToken.None, stopOnTechnicalFailure: true);
                if (!verification.CanHide)
                {
                    var report = WorkspaceFailedTaskReport.FromFollow(verification);
                    ThrowIfTechnicalReport(report);
                    await failedStore.SaveAsync(chat, messageId, report);
                    var details = string.Join("; ", verification.Results
                        .Where(item => item.Check.Status != InstagramTaskCheckStatus.CONFIRMED)
                        .Select(item => $"@{item.TargetUserName}: {item.Check.Detail}"));
                    return new WorkspaceBulkTaskOutcome(messageId, false,
                        verification.Check.Detail + " " + details, false);
                }
                await new WorkspaceSubscriptionTaskVerificationStore(reader.DatabasePath)
                    .PublishAsync(chat, messageId, verification);
                return new WorkspaceBulkTaskOutcome(messageId, true,
                    $"{kindLabel}: проверено {actions.Count} действий, задание скрыто.", false);
            }

            if (kind == WorkspaceTaskKind.STORY)
            {
                var stories = WorkspaceStoryTaskRules.GetStoryOnlyUrls(task.Text, task.PostUrl);
                if (stories.Count == 0)
                    return await FailedAsync("В сообщении нет корректной прямой ссылки на сторис.");
                target = string.Join(", ", stories);
                var storyResult = await browser.CheckStoryLikesAsync(stories, viewer,
                    stage, CancellationToken.None, stopOnTechnicalFailure: true);
                if (!storyResult.CanHide)
                {
                    var storyReport = WorkspaceFailedTaskReport.FromPost(storyResult);
                    ThrowIfTechnicalReport(storyReport);
                    await failedStore.SaveAsync(chat, messageId, storyReport);
                    return new WorkspaceBulkTaskOutcome(messageId, false,
                        "Лайк сторис: не подтверждено. " + storyResult.Like?.Detail, false);
                }
                await new WorkspaceTaskVerificationStore(reader.DatabasePath)
                    .PublishAsync(chat, messageId, storyResult);
                return new WorkspaceBulkTaskOutcome(messageId, true,
                    $"Лайки всех слайдов Stories подтверждены. Задание скрыто.", false);
            }

            var requested = WorkspaceBulkTaskVerificationPolicy.PostChecks(task.Text);
            if (!InstagramTaskVerificationRules.TryGetVerificationPostUrl(task.Text, task.PostUrl, out var url))
                return await FailedAsync("В задании нет корректной ссылки на публикацию или Reels.");
            target = url;
            stage?.Report($"Проверяем публикацию: {url}");
            var result = await browser.CheckTaskActivityAsync(url, viewer,
                requested.Like, requested.Comment, stage, CancellationToken.None,
                null, requested.Save, requested.Repost, stopOnTechnicalFailure: true);
            if (!result.CanHide)
            {
                var report = WorkspaceFailedTaskReport.FromPost(result);
                ThrowIfTechnicalReport(report);
                await failedStore.SaveAsync(chat, messageId, report);
                var details = new[] { result.Like, result.Comment, result.Save, result.Repost }
                    .Where(check => check is { Status: not InstagramTaskCheckStatus.CONFIRMED })
                    .Select(check => check!.Detail);
                return new WorkspaceBulkTaskOutcome(messageId, false,
                    FormatChecks(result) + ". " + string.Join("; ", details), false);
            }
            await new WorkspaceTaskVerificationStore(reader.DatabasePath)
                .PublishAsync(chat, messageId, result);
            return new WorkspaceBulkTaskOutcome(messageId, true,
                FormatChecks(result) + ". Задание скрыто.", false);
        }
        catch (InstagramBrowserSessionException)
        {
            // An interrupted task is neither confirmed nor a business failure.
            throw;
        }
        catch (WorkspaceBulkTechnicalFailureException)
        {
            throw;
        }
        catch (WebDriverException exception) when (InstagramBrowserFailure.IsFatal(exception))
        {
            throw new InstagramBrowserSessionException(
                $"Ошибка Chrome во время массовой проверки: {exception.Message}", exception);
        }
        catch (Exception exception)
        {
            // Do not turn a technical failure (including a SQLite write failure)
            // into "not confirmed" and advance to the next Telegram message.
            throw new WorkspaceBulkTechnicalFailureException(
                $"Техническая ошибка при проверке задания #{messageId}: {exception.Message}", exception);
        }

        async Task<WorkspaceBulkTaskOutcome> FailedAsync(string detail)
        {
            // A report write failure must remain visible in the batch log. Never hide
            // a task whose verification or report persistence failed.
            try
            {
                await failedStore.SaveAsync(chat, messageId,
                    WorkspaceFailedTaskReport.FromError(kindLabel, viewer, target,
                        kind is WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE or WorkspaceTaskKind.MIXED
                            ? [kindLabel] : kind == WorkspaceTaskKind.STORY ? new[] { "Лайк" } : SelectedChecks(task.Text), detail));
            }
            catch (Exception exception)
            {
                // A persistence failure is technical. Do not continue the batch
                // without recording the result of this task.
                throw new WorkspaceBulkTechnicalFailureException(
                    $"Не удалось сохранить отчёт задания #{messageId}: {exception.Message}", exception);
            }
            return new WorkspaceBulkTaskOutcome(messageId, false, detail, false);
        }
    }

    private static void ThrowIfTechnicalReport(WorkspaceFailedTaskReport report)
    {
        if (report.FailureCategory != WorkspaceVerificationFailureCategory.TECHNICAL)
            return;
        // Defensive check: a browser method may return a technical diagnostic
        // rather than throw. Never persist it as a failed Instagram action.
        var detail = report.Checks.FirstOrDefault(check =>
            !check.Confirmed && WorkspaceFailedTaskReport.IsTechnicalDiagnostic(check.Detail))?.Detail;
        throw new WorkspaceBulkTechnicalFailureException(detail ??
            "Техническая ошибка во время проверки задания.");
    }

    private static string FormatChecks(InstagramTaskVerificationResult result)
    {
        var entries = new (string Label, InstagramTaskCheckResult? Result)[]
        {
            ("Лайк", result.Like),
            ("Комментарий", result.Comment),
            ("Сохранение", result.Save),
            ("Репост", result.Repost)
        };
        return string.Join(" · ", entries.Where(entry => entry.Result is not null)
            .Select(entry => $"{entry.Label}: " +
                (entry.Result!.Status == InstagramTaskCheckStatus.CONFIRMED ? "✓" : "не подтверждено")));
    }

    private static string[] SelectedChecks(string? text)
    {
        var selected = WorkspaceBulkTaskVerificationPolicy.PostChecks(text);
        var result = new List<string> { "Лайк" };
        if (selected.Comment) result.Add("Комментарий");
        if (selected.Save) result.Add("Сохранение");
        if (selected.Repost) result.Add("Репост");
        return result.ToArray();
    }
}

public sealed record WorkspaceBulkTaskOutcome(
    long MessageId, bool Hidden, string Detail, bool Skipped);
