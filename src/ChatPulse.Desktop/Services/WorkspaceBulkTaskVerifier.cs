using ChatPulse.Desktop.Settings;

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

    public async Task<IReadOnlyList<long>> GetSnapshotAsync(ConfiguredChat chat,
        bool oldestFirst, Func<bool> stopRequested)
    {
        const int PAGE_SIZE = 60;
        var ids = new List<long>();
        for (var offset = 0; ; offset += PAGE_SIZE)
        {
            if (stopRequested()) break;
            var page = await reader.ReadPageAsync(chat, WorkspaceTaskState.Completed,
                oldestFirst, offset, PAGE_SIZE, includeTotals: false);
            ids.AddRange(page.Tasks.Select(task => task.MessageId));
            if (page.Tasks.Count < PAGE_SIZE) break;
        }
        return ids;
    }

    public async Task<WorkspaceBulkTaskOutcome> VerifyOneAsync(
        ConfiguredChat chat, long messageId, string viewer,
        IProgress<string>? stage = null)
    {
        var task = await reader.ReadCompletedByIdAsync(chat, messageId);
        if (task is null)
            return new WorkspaceBulkTaskOutcome(messageId, false,
                "Задание уже скрыто или больше не находится в разделе «Выполненные».", true);

        var failedStore = new WorkspaceFailedTaskVerificationStore(reader.DatabasePath);
        var kind = WorkspaceTaskKindDetector.Detect(task.Text, task.PostUrl);
        var kindLabel = kind switch
        {
            WorkspaceTaskKind.SUBSCRIBE => "Подписка",
            WorkspaceTaskKind.UNSUBSCRIBE => "Отписка",
            _ => "Публикация"
        };
        string target = task.PostUrl;
        try
        {
            if (kind is WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE)
            {
                var candidates = InstagramFollowTaskRules.GetVerificationTargets(kind, task.Text, task.PostUrl);
                if (candidates.Count == 0)
                    return await FailedAsync("В задании нет Instagram-профилей для проверки.");
                target = string.Join(", ", candidates.Select(name => $"@{name}"));
                var verification = await InstagramFollowTaskRules.VerifyTargetsAsync(viewer, kind, candidates,
                    (name, shouldFollow, token) => browser.CheckOwnFollowingAsync(viewer, name, shouldFollow, token),
                    stage, CancellationToken.None);
                if (!verification.CanHide)
                {
                    await failedStore.SaveAsync(chat, messageId,
                        WorkspaceFailedTaskReport.FromFollow(verification));
                    var details = string.Join("; ", verification.Results
                        .Where(item => item.Check.Status != InstagramTaskCheckStatus.CONFIRMED)
                        .Select(item => $"@{item.TargetUserName}: {item.Check.Detail}"));
                    return new WorkspaceBulkTaskOutcome(messageId, false,
                        verification.Check.Detail + " " + details, false);
                }
                await new WorkspaceSubscriptionTaskVerificationStore(reader.DatabasePath)
                    .PublishAsync(chat, messageId, verification);
                return new WorkspaceBulkTaskOutcome(messageId, true,
                    $"{kindLabel}: проверено {candidates.Count} профилей, задание скрыто.", false);
            }

            var requested = WorkspaceBulkTaskVerificationPolicy.PostChecks(task.Text);
            if (!InstagramTaskVerificationRules.TryGetPostUrl(task.PostUrl, out var url))
                return await FailedAsync("В задании нет корректной ссылки на публикацию или Reels.");
            target = url;
            stage?.Report($"Проверяем публикацию: {url}");
            var result = await browser.CheckTaskActivityAsync(url, viewer,
                requested.Like, requested.Comment, stage, CancellationToken.None,
                null, requested.Save, requested.Repost);
            if (!result.CanHide)
            {
                await failedStore.SaveAsync(chat, messageId, WorkspaceFailedTaskReport.FromPost(result));
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
        catch (Exception exception)
        {
            return await FailedAsync($"Техническая ошибка: {exception.Message}");
        }

        async Task<WorkspaceBulkTaskOutcome> FailedAsync(string detail)
        {
            // A report write failure must remain visible in the batch log. Never hide
            // a task whose verification or report persistence failed.
            try
            {
                await failedStore.SaveAsync(chat, messageId,
                    WorkspaceFailedTaskReport.FromError(kindLabel, viewer, target,
                        kind is WorkspaceTaskKind.SUBSCRIBE or WorkspaceTaskKind.UNSUBSCRIBE
                            ? [kindLabel] : SelectedChecks(task.Text), detail));
            }
            catch (Exception exception)
            {
                detail += $" Отчёт не сохранён: {exception.Message}";
            }
            return new WorkspaceBulkTaskOutcome(messageId, false, detail, false);
        }
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
