using System.Text.Json;
using System.IO;
using System.Diagnostics;
using ChatPulse.Desktop.Settings;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace ChatPulse.Desktop.Services;

/// <summary>One visible Chrome instance with a locally persisted profile. Authentication is manual.</summary>
public sealed class InstagramBrowserService : IDisposable
{
    private readonly string _profileDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChromeDriver? _driver;
    private bool _disposed;

    public InstagramBrowserService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatPulse", "InstagramBrowser")) { }

    public InstagramBrowserService(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        _profileDirectory = Path.GetFullPath(profileDirectory);
    }

    public async Task OpenLoginAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() => GetDriver().Navigate().GoToUrl("https://www.instagram.com/"), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HasSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_driver is null) return false;
            return await Task.Run(() =>
            {
                _driver.Navigate().GoToUrl("https://www.instagram.com/");
                return _driver.Manage().Cookies.GetCookieNamed("sessionid") is not null;
            }, cancellationToken);
        }
        catch (WebDriverException) { return false; }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Reads two lists through the browser UI. Never treats stalled scrolling as proof of completeness
    /// unless the exact count displayed by Instagram also matches collected unique usernames.
    /// </summary>
    public async Task<(InstagramListCollection Followers, InstagramListCollection Following)> CollectListsAsync(
        string targetUserName, CancellationToken cancellationToken = default)
    {
        var target = InstagramUserName.Normalize(targetUserName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                var driver = GetDriver();
                if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null)
                {
                    driver.Navigate().GoToUrl("https://www.instagram.com/");
                    if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null)
                        throw new InvalidOperationException("Сессия Instagram отсутствует. Выполните вход в Chrome.");
                }
                return (ReadList(driver, target, "followers", cancellationToken),
                    ReadList(driver, target, "following", cancellationToken));
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Diagnostic collection of unique follower usernames; does not infer missing subscriptions.</summary>
    public async Task<InstagramListCollection> CollectFollowersAsync(
        string targetUserName, CancellationToken cancellationToken = default)
    {
        var target = InstagramUserName.Normalize(targetUserName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                var driver = GetDriver();
                driver.Navigate().GoToUrl("https://www.instagram.com/");
                if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null)
                    throw new InvalidOperationException("Сессия Instagram отсутствует. Выполните вход в Chrome.");
                return ReadList(driver, target, "followers", cancellationToken);
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Checks one activity-account username using Instagram's list search, without loading
    /// full follower/following lists. No-result is only an observation, not proof of unfollow.
    /// </summary>
    public async Task<InstagramParticipantCheck> CheckParticipantAsync(string targetUserName,
        string activityProfileUrl, CancellationToken cancellationToken = default)
    {
        var target = InstagramUserName.Normalize(targetUserName);
        var member = InstagramActivityProfile.TryGetUserName(activityProfileUrl)
            ?? throw new ArgumentException("Нужна корректная Instagram-ссылка аккаунта активности.",
                nameof(activityProfileUrl));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                var driver = GetDriver();
                driver.Navigate().GoToUrl("https://www.instagram.com/");
                if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null)
                    throw new InvalidOperationException("Сессия Instagram отсутствует. Выполните вход в Chrome.");

                var followers = CheckSingleList(driver, target, member, "followers", cancellationToken);
                var following = CheckSingleList(driver, target, member, "following", cancellationToken);
                return new InstagramParticipantCheck(member, followers, following);
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static InstagramSearchResult CheckSingleList(ChromeDriver driver, string target,
        string member, string kind, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            driver.Navigate().GoToUrl($"https://www.instagram.com/{target}/");

            // Search only the profile counter. A generic "Following" link elsewhere on
            // the Instagram page may open a different screen with no list search.
            var trigger = WaitFor(driver, () => FindParticipantListTrigger(driver, target, kind),
                cancellationToken);
            if (trigger is not null)
            {
                try { trigger.Click(); }
                catch (StaleElementReferenceException)
                {
                    var fresh = WaitFor(driver, () => FindParticipantListTrigger(driver, target, kind),
                        cancellationToken);
                    fresh?.Click();
                }
                catch (ElementClickInterceptedException)
                {
                    var fresh = WaitFor(driver, () => FindParticipantListTrigger(driver, target, kind),
                        cancellationToken);
                    if (fresh is not null)
                        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", fresh);
                }
            }

            // A visible role=dialog is not sufficient: Instagram can show an unrelated
            // modal. Confirm the list title and a usable search input before typing.
            var input = WaitFor(driver, () => FindParticipantDialogSearch(driver, kind), cancellationToken);
            // A visible dialog may be the correct window even when its search field
            // appears later. Do not navigate away from an already opened dialog:
            // doing so obscures the original failure and can open another screen.
            if (input is null && !HasVisibleInstagramDialog(driver) &&
                !HasVisibleLabelledParticipantSearch(driver))
            {
                driver.Navigate().GoToUrl($"https://www.instagram.com/{target}/{kind}/");
                input = WaitFor(driver, () => FindParticipantDialogSearch(driver, kind), cancellationToken);
            }
            if (input is null)
            {
                var label = kind == "following" ? "Подписки" : "Подписчики";
                return new InstagramSearchResult(InstagramSearchStatus.INCONCLUSIVE,
                    $"Не удалось найти поле поиска для окна «{label}». " +
                    DescribeParticipantDialogState(driver, kind));
            }

            input.Click();
            input.SendKeys(Keys.Control + "a");
            input.SendKeys(member);
            var watch = Stopwatch.StartNew();
            var emptyObservedAt = TimeSpan.Zero;
            var emptySamples = 0;
            var lastSearchValue = string.Empty;
            var lastLinkCount = 0;
            var lastRowCount = 0;
            while (watch.Elapsed < TimeSpan.FromSeconds(16))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var observation = ReadSearchObservation(driver, kind);
                    lastSearchValue = observation.SearchValue;
                    lastLinkCount = observation.ProfileLinks.Length;
                    lastRowCount = observation.RowUserNames.Length;
                    if (!string.Equals(observation.SearchValue, member, StringComparison.OrdinalIgnoreCase))
                    {
                        Thread.Sleep(300);
                        continue;
                    }
                    if (InstagramSearchEvidence.Classify(member, observation.ProfileLinks, false,
                            observation.RowUserNames) == InstagramSearchStatus.FOUND)
                        return new InstagramSearchResult(InstagramSearchStatus.FOUND, "Профиль найден.");

                    if (observation.NoResults && !observation.Loading && watch.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        if (emptySamples == 0) emptyObservedAt = watch.Elapsed;
                        emptySamples++;
                        if (emptySamples >= 3 && watch.Elapsed - emptyObservedAt >= TimeSpan.FromSeconds(1))
                            return new InstagramSearchResult(InstagramSearchStatus.NOT_FOUND,
                                "Instagram показал пустой результат поиска.");
                    }
                    else emptySamples = 0;
                }
                catch (StaleElementReferenceException)
                {
                    emptySamples = 0;
                }
                Thread.Sleep(350);
            }
            return new InstagramSearchResult(InstagramSearchStatus.INCONCLUSIVE,
                $"Нет подтверждённого результата поиска за 16 секунд. " +
                $"Значение поля: '{lastSearchValue}'; ссылок в окне: {lastLinkCount}; " +
                $"распознанных строк с username: {lastRowCount}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (WebDriverException exception)
        {
            return new InstagramSearchResult(InstagramSearchStatus.INCONCLUSIVE,
                $"Ошибка Selenium: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return new InstagramSearchResult(InstagramSearchStatus.INCONCLUSIVE, exception.Message);
        }
    }

    /// <summary>
    /// Checks current participants once per direction. Each Instagram list is opened once,
    /// and every observed result is reported immediately so the caller can persist it.
    /// </summary>
    public Task<InstagramBatchRunResult> CheckParticipantsAsync(string targetUserName,
        IReadOnlyList<string> participantUserNames, Action<InstagramBatchProgress> onResult,
        CancellationToken cancellationToken = default)
        => CheckParticipantsAsync(targetUserName, participantUserNames, participantUserNames,
            onResult, cancellationToken);

    /// <summary>
    /// Checks the two directions independently. VIP/admin accounts are omitted from
    /// followers but included in following by the calling eligibility plan.
    /// Each nonempty Instagram list opens only once.
    /// </summary>
    public async Task<InstagramBatchRunResult> CheckParticipantsAsync(string targetUserName,
        IReadOnlyList<string> followsMeUserNames, IReadOnlyList<string> iFollowUserNames,
        Action<InstagramBatchProgress> onResult, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(followsMeUserNames);
        ArgumentNullException.ThrowIfNull(iFollowUserNames);
        ArgumentNullException.ThrowIfNull(onResult);
        var target = InstagramUserName.Normalize(targetUserName);
        var followersMembers = followsMeUserNames.Select(InstagramUserName.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var followingMembers = iFollowUserNames.Select(InstagramUserName.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var total = followersMembers.Length + followingMembers.Length;
        if (total == 0) return new InstagramBatchRunResult(0, 0, null);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                var driver = GetDriver();
                driver.Navigate().GoToUrl("https://www.instagram.com/");
                if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null)
                    throw new InvalidOperationException("Сессия Instagram отсутствует. Выполните вход в Chrome.");

                var done = 0;
                foreach (var (kind, members) in new[]
                         { ("followers", followersMembers), ("following", followingMembers) })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (members.Length == 0) continue;
                    if (!OpenParticipantListForBatch(driver, target, kind, cancellationToken,
                            out var openError))
                        return new InstagramBatchRunResult(done, total,
                            $"Не удалось открыть список {kind}: {openError}");

                    var consecutiveInconclusive = 0;
                    foreach (var member in members)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = SearchParticipantInOpenedList(driver, member, kind,
                            cancellationToken);
                        done++;
                        onResult(new InstagramBatchProgress(member, kind, result, done, total));
                        consecutiveInconclusive = result.Status == InstagramSearchStatus.INCONCLUSIVE
                            ? consecutiveInconclusive + 1 : 0;
                        if (IsInstagramRestrictionVisible(driver))
                            return new InstagramBatchRunResult(done, total,
                                "Instagram сообщил об ограничении действий. Проверка остановлена.");
                        if (consecutiveInconclusive >= 3)
                            return new InstagramBatchRunResult(done, total,
                                "Три подряд неудачные проверки. Остановлено для защиты от неверных результатов.");

                        if (done < total)
                        {
                            WaitWithCancellation(1_800, cancellationToken);
                            if (done % 10 == 0) WaitWithCancellation(5_000, cancellationToken);
                        }
                    }
                }
                return new InstagramBatchRunResult(done, total, null);
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static bool OpenParticipantListForBatch(ChromeDriver driver, string target,
        string kind, CancellationToken cancellationToken, out string error)
    {
        error = string.Empty;
        try
        {
            driver.Navigate().GoToUrl($"https://www.instagram.com/{target}/");
            var trigger = WaitFor(driver, () => FindParticipantListTrigger(driver, target, kind),
                cancellationToken);
            if (trigger is not null)
            {
                try { trigger.Click(); }
                catch (StaleElementReferenceException)
                {
                    var fresh = WaitFor(driver, () => FindParticipantListTrigger(driver, target, kind),
                        cancellationToken);
                    fresh?.Click();
                }
                catch (ElementClickInterceptedException)
                {
                    var fresh = WaitFor(driver, () => FindParticipantListTrigger(driver, target, kind),
                        cancellationToken);
                    if (fresh is not null)
                        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", fresh);
                }
            }
            var input = WaitFor(driver, () => FindParticipantDialogSearch(driver, kind),
                cancellationToken);
            if (input is null && !HasVisibleInstagramDialog(driver) &&
                !HasVisibleLabelledParticipantSearch(driver))
            {
                driver.Navigate().GoToUrl($"https://www.instagram.com/{target}/{kind}/");
                input = WaitFor(driver, () => FindParticipantDialogSearch(driver, kind),
                    cancellationToken);
            }
            if (input is not null) return true;
            error = DescribeParticipantDialogState(driver, kind);
        }
        catch (OperationCanceledException) { throw; }
        catch (WebDriverException exception) { error = exception.Message; }
        return false;
    }

    private static InstagramSearchResult SearchParticipantInOpenedList(ChromeDriver driver,
        string member, string kind, CancellationToken cancellationToken)
    {
        try
        {
            // The input and its container are re-located for every username; never
            // retain an IWebElement across Instagram's dynamic search updates.
            var input = WaitFor(driver, () => FindParticipantDialogSearch(driver, kind),
                cancellationToken);
            if (input is null)
                return new InstagramSearchResult(InstagramSearchStatus.INCONCLUSIVE,
                    "Поле поиска исчезло: " + DescribeParticipantDialogState(driver, kind));
            input.Click();
            input.SendKeys(Keys.Control + "a");
            input.SendKeys(member);

            var watch = Stopwatch.StartNew();
            var emptySamples = 0;
            var firstEmpty = TimeSpan.Zero;
            while (watch.Elapsed < TimeSpan.FromSeconds(16))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var observation = ReadSearchObservation(driver, kind);
                    if (!string.Equals(observation.SearchValue, member,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        WaitWithCancellation(300, cancellationToken);
                        continue;
                    }
                    if (InstagramSearchEvidence.Classify(member, observation.ProfileLinks, false,
                            observation.RowUserNames) == InstagramSearchStatus.FOUND)
                        return new InstagramSearchResult(InstagramSearchStatus.FOUND,
                            "Точное совпадение username в результатах поиска.");
                    if (observation.NoResults && !observation.Loading &&
                        watch.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        if (emptySamples == 0) firstEmpty = watch.Elapsed;
                        emptySamples++;
                        if (emptySamples >= 3 && watch.Elapsed - firstEmpty >= TimeSpan.FromSeconds(1))
                            return new InstagramSearchResult(InstagramSearchStatus.NOT_FOUND,
                                "Instagram показал пустой результат поиска; отписка не подтверждена.");
                    }
                    else emptySamples = 0;
                }
                catch (StaleElementReferenceException) { emptySamples = 0; }
                WaitWithCancellation(350, cancellationToken);
            }
            return new InstagramSearchResult(InstagramSearchStatus.INCONCLUSIVE,
                "За 16 секунд не удалось получить подтверждённый результат поиска.");
        }
        catch (OperationCanceledException) { throw; }
        catch (WebDriverException exception)
        {
            return new InstagramSearchResult(InstagramSearchStatus.INCONCLUSIVE,
                "Ошибка Selenium: " + exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new InstagramSearchResult(InstagramSearchStatus.INCONCLUSIVE, exception.Message);
        }
    }

    private static bool IsInstagramRestrictionVisible(ChromeDriver driver)
    {
        try
        {
            var text = ((IJavaScriptExecutor)driver).ExecuteScript(
                "return document.body?.innerText || ''; ") as string ?? string.Empty;
            return new[] { "Try Again Later", "We restrict certain activity", "Попробуйте позже",
                "Мы ограничиваем определенные действия", "Мы ограничиваем определённые действия",
                "Слишком много запросов" }.Any(phrase =>
                text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
        }
        catch (WebDriverException) { return true; }
    }

    private static void WaitWithCancellation(int milliseconds, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static IWebElement? FindParticipantListTrigger(ChromeDriver driver, string target, string kind)
    {
        var exactLink = driver.FindElements(By.CssSelector("main a[href]"))
            .FirstOrDefault(element => element.Displayed &&
                InstagramListCollectionParser.IsListUrl(element.GetAttribute("href"), target, kind));
        if (exactLink is not null) return exactLink;

        // Do not search global navigation or other buttons in <main>.
        // Only a counter inside the target profile header is a safe fallback.
        var names = kind == "followers"
            ? new[] { "followers", "подписчик" }
            : new[] { "following", "подписок", "подписки" };
        return driver.FindElements(By.CssSelector(
                "main header a, main header button, main header [role='button']"))
            .FirstOrDefault(element => element.Displayed && names.Any(word =>
                $"{element.Text} {element.GetAttribute("aria-label")}".Contains(word,
                    StringComparison.OrdinalIgnoreCase)));
    }

    private static IWebElement? FindParticipantListDialog(ChromeDriver driver, string kind)
    {
        foreach (var dialog in driver.FindElements(By.CssSelector("div[role='dialog']")))
        {
            if (!dialog.Displayed) continue;
            var headings = dialog.FindElements(By.CssSelector("h1, h2, h3, [role='heading']"))
                .Where(element => element.Displayed).Select(element => element.Text).ToArray();
            // Instagram may render the list title as a plain div/span while using
            // heading elements for unrelated content inside the same dialog.
            // Evaluate both sources, rather than ignoring the dialog text when
            // any heading exists.
            var bodyLines = dialog.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (InstagramParticipantDialogTitle.MatchesDialog(kind, headings, bodyLines))
                return dialog;
        }
        return null;
    }

    // Instagram's localized list search uses this aria-label, which is more stable
    // than obfuscated CSS classes or the input's position in the modal.
    private const string ParticipantSearchInputSelector =
        "input[aria-label='Ввод поискового запроса'], input[aria-label='Search input'], " +
        "input[placeholder='Поиск'], input[placeholder='Search']";

    private const string ParticipantFallbackInputSelector =
        "input[type='text'], input[type='search'], input:not([type]), [role='textbox']";

    private static bool HasVisibleInstagramDialog(ChromeDriver driver)
        => driver.FindElements(By.CssSelector("div[role='dialog']"))
            .Any(element => element.Displayed);

    private static bool HasVisibleLabelledParticipantSearch(ChromeDriver driver)
        => driver.FindElements(By.CssSelector(ParticipantSearchInputSelector))
            .Any(element => element.Displayed);

    private static IWebElement? FindParticipantSearchRoot(ChromeDriver driver, string kind)
    {
        var titledDialog = FindParticipantListDialog(driver, kind);
        if (titledDialog is not null && FindUsableSearchInput(titledDialog) is not null)
            return titledDialog;

        // A list can be rendered in a portal, with the input outside a [role=dialog]
        // subtree. In that case find the *local* overlay surrounding the labelled
        // field and the requested title. Never use the page-wide Instagram search.
        var root = ((IJavaScriptExecutor)driver).ExecuteScript("""
            const kind = arguments[0];
            const titles = kind === 'following'
                ? ['подписки', 'ваши подписки', 'following', 'your following']
                : ['подписчики', 'followers'];
            const fields = document.querySelectorAll(
                'input[aria-label="Ввод поискового запроса"], ' +
                'input[aria-label="Search input"], ' +
                'input[placeholder="Поиск"], input[placeholder="Search"]');
            const visible = element => element.getClientRects().length > 0 &&
                getComputedStyle(element).visibility !== 'hidden';
            const hasTitle = root => Array.from(root.querySelectorAll(
                    'h1, h2, h3, [role="heading"], div, span, strong, b'))
                .some(element => visible(element) && titles.includes(
                    (element.innerText || '').trim().toLowerCase()));
            for (const input of fields) {
                if (!visible(input) || input.disabled) continue;
                let nearest = null;
                for (let node = input.parentElement, depth = 0;
                     node && node !== document.body && depth < 16;
                     node = node.parentElement, depth++) {
                    if (node.matches('main, nav, header, [role="navigation"]')) break;
                    if (!hasTitle(node)) continue;
                    nearest ??= node;
                    // Prefer a container that includes the list as well as the title.
                    if (node.querySelector('a[href]')) return node;
                }
                if (nearest) return nearest;
            }
            return null;
            """, kind) as IWebElement;

        return root ?? titledDialog;
    }

    private static IWebElement? FindUsableSearchInput(IWebElement root)
    {
        var specific = root.FindElements(By.CssSelector(ParticipantSearchInputSelector))
            .FirstOrDefault(element => element.Displayed && element.Enabled);
        return specific ?? root.FindElements(By.CssSelector(ParticipantFallbackInputSelector))
            .FirstOrDefault(element => element.Displayed && element.Enabled);
    }

    private static string DescribeParticipantDialogState(ChromeDriver driver, string kind)
    {
        try
        {
            var dialogs = driver.FindElements(By.CssSelector("div[role='dialog']"))
                .Where(element => element.Displayed).ToArray();
            var matching = dialogs.Count(dialog =>
            {
                var headings = dialog.FindElements(By.CssSelector("h1, h2, h3, [role='heading']"))
                    .Where(element => element.Displayed).Select(element => element.Text);
                var lines = dialog.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return InstagramParticipantDialogTitle.MatchesDialog(kind, headings, lines);
            });
            var visibleFields = driver.FindElements(By.CssSelector(ParticipantSearchInputSelector))
                .Count(element => element.Displayed);
            var hasRoot = FindParticipantSearchRoot(driver, kind) is not null;
            return $"Видимых окон role=dialog: {dialogs.Length}; " +
                $"окон с нужным заголовком: {matching}; " +
                $"полей с Instagram aria-label: {visibleFields}; " +
                $"контекст нужного списка найден: {(hasRoot ? "да" : "нет")}.";
        }
        catch (WebDriverException)
        {
            return "Не удалось получить состояние окна после обновления страницы.";
        }
    }

    private static IWebElement? FindParticipantDialogSearch(ChromeDriver driver, string kind)
    {
        var root = FindParticipantSearchRoot(driver, kind);
        return root is null ? null : FindUsableSearchInput(root);
    }

    private static (string SearchValue, string[] ProfileLinks, string[] RowUserNames,
        bool NoResults, bool Loading) ReadSearchObservation(ChromeDriver driver, string kind)
    {
        var dialog = FindParticipantSearchRoot(driver, kind);
        if (dialog is null) return (string.Empty, [], [], false, false);
        var json = ((IJavaScriptExecutor)driver).ExecuteScript("""
            const dialog = arguments[0];
            const input = dialog.querySelector(
                'input[aria-label="Ввод поискового запроса"], ' +
                'input[aria-label="Search input"], ' +
                'input[placeholder="Поиск"], input[placeholder="Search"], ' +
                'input[type="text"], input[type="search"], input:not([type]), [role="textbox"]');
            const isVisible = el => el.getClientRects().length > 0 &&
                getComputedStyle(el).visibility !== 'hidden';
            const rowNames = new Set();

            // Instagram can render a user row as clickable divs instead of <a href>.
            // Only accept a visible username in a result row containing an avatar,
            // a profile link or an action button. Input values never count as rows.
            for (const leaf of dialog.querySelectorAll('a, span, div, strong, b')) {
                if (leaf.children.length > 0 || !isVisible(leaf)) continue;
                const name = (leaf.textContent || '').trim().replace(/^@/, '');
                if (!/^[a-zA-Z0-9._]{1,30}$/.test(name)) continue;
                let container = leaf.parentElement;
                for (let depth = 0; container && container !== dialog && depth < 7;
                    container = container.parentElement, depth++) {
                    // Do not mistake text around the search field for a result.
                    if (container.querySelector('input')) break;
                    if (container.querySelector('img, canvas, a[href], button, [role="button"]')) {
                        rowNames.add(name);
                        break;
                    }
                }
            }
            const text = (dialog.innerText || '').toLowerCase();
            const empty = ['no results found', 'no results', 'no users found',
                'ничего не найдено', 'нет результатов', 'результатов нет']
                .some(message => text.includes(message));
            const loading = !!dialog.querySelector('[role="progressbar"], [aria-label="Loading..."], [aria-label="Загрузка"]');
            return JSON.stringify({value: input?.value || '',
                links: Array.from(dialog.querySelectorAll('a[href]'), link => link.href),
                names: Array.from(rowNames), empty, loading});
            """, dialog) as string;
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Не удалось прочитать результаты поиска.");
        using var document = JsonDocument.Parse(json);
        var node = document.RootElement;
        return (node.GetProperty("value").GetString() ?? string.Empty,
            node.GetProperty("links").EnumerateArray()
                .Select(item => item.GetString()).OfType<string>().ToArray(),
            node.GetProperty("names").EnumerateArray()
                .Select(item => item.GetString()).OfType<string>().ToArray(),
            node.GetProperty("empty").GetBoolean(), node.GetProperty("loading").GetBoolean());
    }

    private static InstagramListCollection ReadList(ChromeDriver driver, string target, string kind,
        CancellationToken cancellationToken)
    {
        driver.Navigate().GoToUrl($"https://www.instagram.com/{target}/");
        // Instagram uses different clickable markup for these counters across UI versions.
        // Try the profile link first, then a labelled button, and finally the direct route.
        var trigger = WaitFor(driver, () => FindListTrigger(driver, target, kind), cancellationToken);
        int? expected = null;
        if (trigger is not null)
        {
            try { expected = InstagramListCollectionParser.ParseExactCount(trigger.Text); }
            catch (StaleElementReferenceException) { /* The counter was replaced during loading. */ }
            try { trigger.Click(); }
            catch (StaleElementReferenceException)
            {
                // The original counter was replaced; locate a fresh element.
                var fresh = WaitFor(driver, () => FindListTrigger(driver, target, kind), cancellationToken);
                fresh?.Click();
            }
            catch (ElementClickInterceptedException)
            {
                var fresh = WaitFor(driver, () => FindListTrigger(driver, target, kind), cancellationToken);
                if (fresh is not null)
                    ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", fresh);
            }
        }

        var dialog = WaitFor(driver, () => FindListDialog(driver), cancellationToken);
        if (dialog is null)
        {
            // A profile may expose the counter as non-anchor markup. Instagram can still
            // serve the list at its canonical route; try that before reporting failure.
            driver.Navigate().GoToUrl($"https://www.instagram.com/{target}/{kind}/");
            dialog = WaitFor(driver, () => FindListDialog(driver), cancellationToken);
        }
        if (dialog is null)
            throw new InvalidOperationException($"Instagram не открыл список {kind} профиля @{target}. " +
                "Проверьте, что список доступен в браузере, и повторите попытку.");

        // Attach before waiting for initial rows: every link added later is captured
        // immediately, even if Instagram removes the row before the next WebDriver poll.
        StartProfileObserver(driver);
        var initiallyPopulated = WaitForProfileLinks(driver, cancellationToken);

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observerLinkCount = DrainObservedLinks(driver, urls);
        var diagnostic = new List<string>();
        var reachedExpectedCount = false;
        var firstSnapshot = string.Empty;
        var lastSnapshot = string.Empty;
        var maxProfileLinksInDom = 0;
        var maxRowMarkersInDom = 0;
        var firstSnapshotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastSnapshotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstSnapshotDetails = string.Empty;
        var lastSnapshotDetails = string.Empty;
        var maxProfileLinksInScrollableRegion = 0;
        var maxUniqueProfilesInScrollableRegion = 0;
        var maxDuplicateProfileLinks = 0;
        var maxAnchorsOutsideScrollableRegion = 0;

        // Instagram may virtualize rows and discard them from the DOM while scrolling.
        // Capture every visible batch before moving a short distance instead of jumping
        // to scrollHeight, which can skip intermediate batches entirely.
        for (var pass = 1; pass <= 2; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pass == 2)
            {
                var reset = ((IJavaScriptExecutor)driver).ExecuteScript("""
                    const root = document.querySelector('div[role="dialog"]');
                    if (!root) return false;
                    const scrollers = [root, ...root.querySelectorAll('*')].filter(x =>
                        x.scrollHeight > x.clientHeight + 4 && x.clientHeight > 0);
                    const scroller = scrollers.sort((a,b) =>
                        (b.scrollHeight-b.clientHeight)-(a.scrollHeight-a.clientHeight))[0];
                    if (!scroller) return false;
                    scroller.scrollTop = 0;
                    return true;
                    """);
                if (reset is not true)
                {
                    diagnostic.Add("Повторный проход: область прокрутки недоступна.");
                    break;
                }
                WaitForListToSettle(driver, cancellationToken, atEnd: false);
            }

            var passStart = InstagramListCollectionParser.ParseProfileLinks(urls).Count;
            // Keep a tail of usernames from the *visible viewport*, not the entire DOM.
            // Instagram may retain off-screen links and then replace them on demand.
            var previousVisibleTail = new List<string>();
            var lostOverlap = 0;
            var recoveredOverlap = 0;
            var recoveryAttempts = 0;
            var stagnantAtEnd = 0;
            var counts = new List<string>();
            var previousCount = passStart;
            var reason = "достигнут предел шагов";
            for (var step = 1; step <= 100; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Read links and DOM diagnostics in one JavaScript call. No IWebElement
                // references survive a scroll, and no usernames are logged in diagnostics.
                var json = ((IJavaScriptExecutor)driver).ExecuteScript("""
                    const dialog = document.querySelector('div[role="dialog"]');
                    if (!dialog) return null;
                    const anchors = Array.from(dialog.querySelectorAll('a'));
                    const scrollers = [dialog, ...dialog.querySelectorAll('*')].filter(x =>
                        x.scrollHeight > x.clientHeight + 4 && x.clientHeight > 0);
                    const scroller = scrollers.sort((a,b) =>
                        (b.scrollHeight-b.clientHeight)-(a.scrollHeight-a.clientHeight))[0] || null;
                    const entries = anchors.map(a => ({
                        href: a.href || '',
                        visible: Boolean(a.getClientRects().length),
                        inScroller: Boolean(scroller && scroller.contains(a)),
                        // Intersect the actual scroll viewport, rather than trusting CSS visibility.
                        inViewport: Boolean(scroller && scroller.contains(a) && (() => {
                            const aRect = a.getBoundingClientRect();
                            const sRect = scroller.getBoundingClientRect();
                            return aRect.bottom > sRect.top + 2 && aRect.top < sRect.bottom - 2;
                        })()),
                        // Structural diagnostics only; display names are not usernames.
                        inSemanticRow: Boolean(a.closest('li, [role="listitem"]'))
                    }));
                    return JSON.stringify({
                        entries,
                        hasScroller: Boolean(scroller),
                        rowMarkers: dialog.querySelectorAll('[role="listitem"], li').length
                    });
                    """) as string;
                if (json is null)
                    throw new InvalidOperationException("Окно Instagram закрылось во время чтения списка.");
                using var snapshot = JsonDocument.Parse(json);
                var entries = snapshot.RootElement.GetProperty("entries");
                var totalAnchors = 0;
                var withoutHref = 0;
                var snapshotHrefs = new List<string>();
                var profileAnchors = 0;
                var visibleProfileAnchors = 0;
                var profileHrefsInScroller = new List<string>();
                var visibleViewportHrefs = new List<string>();
                var profileAnchorsInSemanticRows = 0;
                var linksOutsideScroller = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    totalAnchors++;
                    var href = entry.GetProperty("href").GetString();
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        withoutHref++;
                        continue;
                    }
                    snapshotHrefs.Add(href);
                    if (!entry.GetProperty("inScroller").GetBoolean())
                        linksOutsideScroller++;
                    if (InstagramListCollectionParser.ParseProfileLinks([href]).Count == 0)
                        continue;
                    // Keep names from the complete dialog for comparison with the
                    // scrolling container. Never infer identity from display text.
                    urls.Add(href);
                    profileAnchors++;
                    if (entry.GetProperty("inScroller").GetBoolean())
                        profileHrefsInScroller.Add(href);
                    if (entry.GetProperty("inViewport").GetBoolean())
                        visibleViewportHrefs.Add(href);
                    if (entry.GetProperty("inSemanticRow").GetBoolean())
                        profileAnchorsInSemanticRows++;
                    if (entry.GetProperty("visible").GetBoolean())
                        visibleProfileAnchors++;
                }
                observerLinkCount += DrainObservedLinks(driver, urls);
                var rowMarkers = snapshot.RootElement.GetProperty("rowMarkers").GetInt32();
                maxProfileLinksInDom = Math.Max(maxProfileLinksInDom, profileAnchors);
                maxRowMarkersInDom = Math.Max(maxRowMarkersInDom, rowMarkers);
                maxProfileLinksInScrollableRegion = Math.Max(maxProfileLinksInScrollableRegion,
                    profileHrefsInScroller.Count);
                maxUniqueProfilesInScrollableRegion = Math.Max(maxUniqueProfilesInScrollableRegion,
                    InstagramListCollectionParser.ParseProfileLinks(profileHrefsInScroller).Count);
                maxDuplicateProfileLinks = Math.Max(maxDuplicateProfileLinks,
                    profileAnchors - InstagramListCollectionParser.ParseProfileLinks(snapshotHrefs).Count);
                maxAnchorsOutsideScrollableRegion = Math.Max(maxAnchorsOutsideScrollableRegion, linksOutsideScroller);
                var snapshotSummary = $"ссылок {totalAnchors}, без адреса {withoutHref}, " +
                    $"ссылок на профили {profileAnchors}, видимых профилей {visibleProfileAnchors}, " +
                    $"прочих ссылок {totalAnchors - withoutHref - profileAnchors}, " +
                    $"элементов li/listitem {rowMarkers}, " +
                    $"профильных ссылок в области прокрутки {profileHrefsInScroller.Count}, " +
                    $"профильных ссылок в семантических строках {profileAnchorsInSemanticRows}, " +
                    $"ссылок вне области прокрутки {linksOutsideScroller}";
                var uniqueHrefs = snapshotHrefs.Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var snapshotNames = InstagramListCollectionParser.ParseProfileLinks(snapshotHrefs);
                var snapshotDetails = $"уникальных адресов {uniqueHrefs}, повторных ссылок " +
                    $"{snapshotHrefs.Count - uniqueHrefs}, уникальных имён {snapshotNames.Count}";
                if (string.IsNullOrEmpty(firstSnapshot))
                {
                    firstSnapshot = snapshotSummary;
                    firstSnapshotDetails = snapshotDetails;
                    firstSnapshotNames.UnionWith(snapshotNames);
                }
                lastSnapshot = snapshotSummary;
                lastSnapshotDetails = snapshotDetails;
                lastSnapshotNames.Clear();
                lastSnapshotNames.UnionWith(snapshotNames);

                // Profile links typically occur twice per row. Distinct preserves visual order,
                // and parsing the href avoids confusing a display name with a username.
                var visibleNames = visibleViewportHrefs
                    .SelectMany(href => InstagramListCollectionParser.ParseProfileLinks([href]))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (previousVisibleTail.Count > 0 && visibleNames.Count > 0)
                {
                    var overlap = previousVisibleTail.Any(name => visibleNames.Contains(name,
                        StringComparer.OrdinalIgnoreCase));
                    if (!overlap)
                    {
                        lostOverlap++;
                        // Revisit the skipped band once, without changing cumulative results.
                        // A missing anchor can also be caused by Instagram reordering its list.
                        if (recoveryAttempts < 3)
                        {
                            recoveryAttempts++;
                            var recovered = ((IJavaScriptExecutor)driver).ExecuteScript("""
                                const root = document.querySelector('div[role="dialog"]');
                                if (!root) return false;
                                const candidates = [root, ...root.querySelectorAll('*')].filter(x =>
                                    x.scrollHeight > x.clientHeight + 4 && x.clientHeight > 0);
                                const scroller = candidates.sort((a,b) =>
                                    (b.scrollHeight-b.clientHeight)-(a.scrollHeight-a.clientHeight))[0];
                                if (!scroller) return false;
                                scroller.scrollTop = Math.max(0, scroller.scrollTop - 120);
                                return true;
                                """);
                            if (recovered is true)
                            {
                                WaitForNextProfiles(driver, urls, cancellationToken, atEnd: false);
                                recoveredOverlap++;
                                // Observe the corrected location next iteration; do not reset
                                // the previous checkpoint until we regain a real overlap.
                                continue;
                            }
                        }
                    }
                    else
                    {
                        recoveryAttempts = 0;
                    }
                }
                if (visibleNames.Count > 0)
                    previousVisibleTail = visibleNames.TakeLast(3).ToList();
                var count = InstagramListCollectionParser.ParseProfileLinks(urls).Count;
                counts.Add($"{step}:{count}");
                if (expected is { } total && count == total)
                {
                    reachedExpectedCount = true;
                    reason = "количество совпало со счётчиком профиля";
                    break;
                }

                var scrollState = ((IJavaScriptExecutor)driver).ExecuteScript("""
                    const root = document.querySelector('div[role="dialog"]');
                    if (!root) return 'dialog-closed';
                    const scrollers = [root, ...root.querySelectorAll('*')].filter(x =>
                        x.scrollHeight > x.clientHeight + 4 && x.clientHeight > 0);
                    const scroller = scrollers.sort((a,b) =>
                        (b.scrollHeight-b.clientHeight)-(a.scrollHeight-a.clientHeight))[0];
                    if (!scroller) return 'no-scroller';
                    const last = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
                    const old = scroller.scrollTop;
                    // Advance less than half of most viewports, preserving row overlap.
                    const delta = Math.min(120, Math.max(50, scroller.clientHeight * 0.35));
                    scroller.scrollTop = Math.min(last, old + delta);
                    return scroller.scrollTop >= last - 2 ? 'at-end' : 'scrolling';
                    """) as string;
                if (scrollState == "dialog-closed")
                    throw new InvalidOperationException("Окно Instagram закрылось во время прокрутки.");
                if (scrollState == "no-scroller")
                {
                    reason = "область прокрутки не найдена";
                    break;
                }

                // Read every rendered batch while waiting. Instagram sometimes inserts
                // profiles several seconds after the scroll, without showing a spinner.
                // Do not scroll again immediately just because the DOM is briefly stable.
                var newlyObserved = WaitForNextProfiles(driver, urls, cancellationToken,
                    scrollState == "at-end");
                observerLinkCount += DrainObservedLinks(driver, urls);
                var countAfterWait = InstagramListCollectionParser.ParseProfileLinks(urls).Count;
                if (newlyObserved > 0)
                    diagnostic.Add($"Проход {pass}, шаг {step}: после ожидания добавлено " +
                        $"{newlyObserved} имён; всего {countAfterWait}.");
                if (expected is { } expectedCount && countAfterWait == expectedCount)
                {
                    reachedExpectedCount = true;
                    reason = "количество совпало со счётчиком профиля после ожидания";
                    break;
                }
                if (scrollState == "at-end")
                    stagnantAtEnd = countAfterWait == previousCount ? stagnantAtEnd + 1 : 0;
                else
                    stagnantAtEnd = 0;
                previousCount = countAfterWait;
                if (stagnantAtEnd >= 5)
                {
                    reason = "нет новых имён у конца списка";
                    break;
                }
            }

            var passEnd = InstagramListCollectionParser.ParseProfileLinks(urls).Count;
            diagnostic.Add($"Проход {pass}: было {passStart}, стало {passEnd}, новых {passEnd - passStart}; {reason}. " +
                $"Потеря перекрытия: {lostOverlap}; попыток восстановления: {recoveredOverlap}. " +
                $"Шаги (номер: уникальных имён): {string.Join(", ", counts)}.");
            if (reachedExpectedCount) break;
        }

        observerLinkCount += DrainObservedLinks(driver, urls);
        StopProfileObserver(driver);
        var finalNames = InstagramListCollectionParser.ParseProfileLinks(urls);
        diagnostic.Add($"Наблюдатель DOM: {observerLinkCount} новых URL за один проход " +
            "(включая дубликаты имён); повторных обходов нет.");
        diagnostic.Insert(0, $"Ожидание первоначальной загрузки профилей: " +
            (initiallyPopulated ? "ссылки появились" : "тайм-аут, продолжаем диагностику") + ". " +
            $"Первый снимок окна: {firstSnapshot}; {firstSnapshotDetails}. " +
            $"Последний снимок окна: {lastSnapshot}; {lastSnapshotDetails}. " +
            $"Совпадающих имён в первом и последнем снимках: " +
            $"{firstSnapshotNames.Count(name => lastSnapshotNames.Contains(name))}. " +
            $"Максимум ссылок на профили в одном снимке: {maxProfileLinksInDom}; " +
            $"максимум элементов li/listitem: {maxRowMarkersInDom}; " +
            $"максимум профильных ссылок в области прокрутки: {maxProfileLinksInScrollableRegion}; " +
            $"максимум уникальных имён в области прокрутки: {maxUniqueProfilesInScrollableRegion}; " +
            $"максимум повторных профильных ссылок: {maxDuplicateProfileLinks}; " +
            $"максимум ссылок вне области прокрутки: {maxAnchorsOutsideScrollableRegion}. " +
            "Это диагностические показатели разметки, не доказательство числа подписчиков.");

        // Local-only diagnostic file: lets a user inspect which accounts were actually
        // extracted without exposing usernames in the UI or sending them anywhere.
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatPulse", "Diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                $"instagram-{kind}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            File.WriteAllLines(path,
                new[]
                {
                    $"Instagram @{target}: {kind}",
                    $"Ожидается по счётчику: {expected?.ToString() ?? "неизвестно"}",
                    $"Найдено уникальных имён: {finalNames.Count}",
                    "Имена из первого снимка:",
                    string.Join(", ", firstSnapshotNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
                    "Имена из последнего снимка:",
                    string.Join(", ", lastSnapshotNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
                    "Все уникальные имена:",
                }.Concat(finalNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)));
            diagnostic.Add($"Локальный файл с именами для диагностики: {path}");
        }
        catch (IOException ex)
        {
            diagnostic.Add($"Не удалось записать диагностический файл: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            diagnostic.Add($"Нет доступа для записи диагностического файла: {ex.Message}");
        }

        return new InstagramListCollection(finalNames, expected, reachedExpectedCount,
            string.Join("\n", diagnostic));
    }

    // The observer is installed inside Instagram's own page and is limited to the
    // followers/following dialog. It records URL strings, not IWebElement references.
    // It does not fetch Instagram APIs or trigger additional scrolling.
    private static void StartProfileObserver(ChromeDriver driver)
    {
        var installed = ((IJavaScriptExecutor)driver).ExecuteScript("""
            const dialog = document.querySelector('div[role="dialog"]');
            if (!dialog) return false;
            if (window.__chatPulseProfileObserver) {
                window.__chatPulseProfileObserver.disconnect();
            }
            const seen = new Set();
            const collect = node => {
                if (!(node instanceof Element)) return;
                if (node.matches('a[href]')) seen.add(node.href);
                for (const a of node.querySelectorAll('a[href]')) seen.add(a.href);
            };
            collect(dialog);
            const observer = new MutationObserver(records => {
                for (const record of records) {
                    if (record.type === 'attributes') {
                        collect(record.target);
                    } else {
                        for (const node of record.addedNodes) collect(node);
                    }
                }
            });
            observer.observe(dialog, {subtree:true, childList:true,
                attributes:true, attributeFilter:['href']});
            window.__chatPulseProfileObserver = observer;
            window.__chatPulseProfileUrls = seen;
            return true;
            """);
        if (installed is not true)
            throw new InvalidOperationException("Не удалось запустить наблюдение за окном Instagram.");
    }

    private static int DrainObservedLinks(ChromeDriver driver, HashSet<string> urls)
    {
        var json = ((IJavaScriptExecutor)driver).ExecuteScript("""
            const seen = window.__chatPulseProfileUrls;
            if (!seen) return '[]';
            const result = JSON.stringify(Array.from(seen));
            seen.clear();
            return result;
            """) as string;
        if (string.IsNullOrEmpty(json)) return 0;
        using var document = JsonDocument.Parse(json);
        var count = 0;
        foreach (var value in document.RootElement.EnumerateArray())
        {
            var href = value.GetString();
            if (string.IsNullOrWhiteSpace(href) ||
                InstagramListCollectionParser.ParseProfileLinks([href]).Count == 0)
                continue;
            urls.Add(href);
            count++;
        }
        return count;
    }

    private static void StopProfileObserver(ChromeDriver driver)
    {
        ((IJavaScriptExecutor)driver).ExecuteScript("""
            window.__chatPulseProfileObserver?.disconnect();
            delete window.__chatPulseProfileObserver;
            delete window.__chatPulseProfileUrls;
            """);
    }

    /// <summary>
    /// After a scroll, keep sampling the dialog for new profile URLs. A spinner may
    /// be absent, and a stable DOM alone does not mean the next batch has arrived.
    /// Every sample contributes to the cumulative set, even if Instagram removes
    /// rows from the DOM before the next sample.
    /// </summary>
    private static int WaitForNextProfiles(ChromeDriver driver, HashSet<string> urls,
        CancellationToken cancellationToken, bool atEnd)
    {
        var initialCount = InstagramListCollectionParser.ParseProfileLinks(urls).Count;
        var timeout = atEnd ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(8);
        var watch = Stopwatch.StartNew();
        var lastChange = TimeSpan.Zero;
        var sawNew = false;
        var loading = false;
        var previousCount = initialCount;
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(250);
            string? json;
            try
            {
                json = ((IJavaScriptExecutor)driver).ExecuteScript("""
                    const root = document.querySelector('div[role="dialog"]');
                    if (!root) return null;
                    const visible = node => Boolean(node.getClientRects().length);
                    const loading = Array.from(root.querySelectorAll(
                        '[role="progressbar"], [aria-busy="true"], '
                        + '[aria-label*="loading" i], [aria-label*="загруз" i], '
                        + 'svg[aria-label*="loading" i]')).some(visible);
                    const urls = Array.from(root.querySelectorAll('a[href]'), a => a.href)
                        .filter(Boolean);
                    return JSON.stringify({loading, urls});
                    """) as string;
            }
            catch (StaleElementReferenceException)
            {
                continue;
            }
            if (json is null)
                throw new InvalidOperationException("Окно Instagram закрылось во время ожидания профилей.");
            using var snapshot = JsonDocument.Parse(json);
            loading = snapshot.RootElement.GetProperty("loading").GetBoolean();
            var current = snapshot.RootElement.GetProperty("urls").EnumerateArray()
                .Select(item => item.GetString())
                .Where(href => !string.IsNullOrWhiteSpace(href))
                .Select(href => href!)
                .ToArray();
            foreach (var href in current)
            {
                if (InstagramListCollectionParser.ParseProfileLinks([href]).Count > 0)
                    urls.Add(href);
            }
            DrainObservedLinks(driver, urls);
            var currentCount = InstagramListCollectionParser.ParseProfileLinks(urls).Count;
            if (currentCount > previousCount)
            {
                sawNew = true;
                lastChange = watch.Elapsed;
            }
            previousCount = currentCount;
            if (sawNew && !loading && watch.Elapsed - lastChange >= TimeSpan.FromSeconds(1.2))
                break;
        }
        return InstagramListCollectionParser.ParseProfileLinks(urls).Count - initialCount;
    }

    /// <summary>
    /// Wait until the Instagram dialog has no active loading indicator and its
    /// profile links / scroll geometry remain stable. The spinner is optional:
    /// absence of one does not imply that asynchronous rendering has finished.
    /// This wait is bounded; a timeout is not proof that the list is complete.
    /// </summary>
    private static void WaitForListToSettle(ChromeDriver driver,
        CancellationToken cancellationToken, bool atEnd)
    {
        var watch = Stopwatch.StartNew();
        var timeout = atEnd ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(8);
        var stableFor = TimeSpan.Zero;
        string? previous = null;
        var lastCheck = watch.Elapsed;
        var sawLoader = false;
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(250);
            var now = watch.Elapsed;
            // Work with a fresh DOM snapshot, never a retained IWebElement.
            string? result;
            try
            {
                result = ((IJavaScriptExecutor)driver).ExecuteScript("""
                    const root = document.querySelector('div[role="dialog"]');
                    if (!root) return null;
                    const scrollers = [root, ...root.querySelectorAll('*')].filter(x =>
                        x.scrollHeight > x.clientHeight + 4 && x.clientHeight > 0);
                    const scroller = scrollers.sort((a,b) =>
                        (b.scrollHeight-b.clientHeight)-(a.scrollHeight-a.clientHeight))[0] || root;
                    const visible = node => Boolean(node.getClientRects().length);
                    const loading = Array.from(root.querySelectorAll(
                        '[role="progressbar"], [aria-busy="true"], '
                        + '[aria-label*="loading" i], [aria-label*="загруз" i], '
                        + 'svg[aria-label*="loading" i]')).some(visible);
                    const links = Array.from(root.querySelectorAll('a[href]'), x => x.href)
                        .filter(Boolean).sort();
                    return JSON.stringify({loading,
                        fingerprint: JSON.stringify([links, Math.round(scroller.scrollTop),
                            scroller.scrollHeight, scroller.clientHeight])});
                    """) as string;
            }
            catch (StaleElementReferenceException)
            {
                previous = null;
                stableFor = TimeSpan.Zero;
                lastCheck = now;
                continue;
            }
            if (result is null)
                throw new InvalidOperationException("Окно Instagram закрылось при ожидании загрузки списка.");
            using var state = JsonDocument.Parse(result);
            var isLoading = state.RootElement.GetProperty("loading").GetBoolean();
            var fingerprint = state.RootElement.GetProperty("fingerprint").GetString();
            if (isLoading)
                sawLoader = true;
            if (isLoading || fingerprint != previous)
                stableFor = TimeSpan.Zero;
            else
                stableFor += now - lastCheck;
            previous = fingerprint;
            lastCheck = now;
            // Wait slightly longer when a spinner was observed: it may hide
            // before the rows from the new batch are inserted into the DOM.
            var required = sawLoader ? TimeSpan.FromMilliseconds(900)
                : TimeSpan.FromMilliseconds(1400);
            if (!isLoading && stableFor >= required && watch.Elapsed >= TimeSpan.FromMilliseconds(800))
                return;
        }
        // Timed out: the caller continues collecting, without declaring completeness.
    }

    private static bool WaitForProfileLinks(ChromeDriver driver, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Read the whole dialog on every attempt without holding IWebElement
            // references across Instagram's DOM updates.
            var urls = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const dialog = document.querySelector('div[role="dialog"]');
                if (!dialog) return [];
                return Array.from(dialog.querySelectorAll('a[href]'), a => a.href);
                """) as IReadOnlyCollection<object>;
            if (urls is not null && InstagramListCollectionParser.ParseProfileLinks(
                    urls.OfType<string>()).Count > 0)
                return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private static IWebElement? FindListTrigger(ChromeDriver driver, string target, string kind)
    {
        // Do not depend on a single exact href: Instagram can use absolute URLs,
        // query parameters, nested elements or accessible buttons instead of anchors.
        var links = driver.FindElements(By.CssSelector("a[href]"));
        var link = links.FirstOrDefault(element =>
            InstagramListCollectionParser.IsListUrl(element.GetAttribute("href"), target, kind));
        if (link is not null) return link;

        var words = kind == "followers"
            ? new[] { "followers", "подписчик" }
            : new[] { "following", "подписк" };
        return driver.FindElements(By.CssSelector("header a, header button, main a, main button, header [role='button'], main [role='button']"))
            .FirstOrDefault(element =>
            {
                var label = $"{element.Text} {element.GetAttribute("aria-label")}";
                return words.Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase)) &&
                    element.Displayed;
            });
    }

    private static IWebElement? FindListDialog(ChromeDriver driver)
        => driver.FindElements(By.CssSelector("div[role='dialog']"))
            .FirstOrDefault(element => element.Displayed);

    private static IWebElement? WaitFor(ChromeDriver driver, Func<IWebElement?> find,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 20; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var element = find();
                if (element is not null) return element;
            }
            catch (StaleElementReferenceException) { /* ignore */}
            Thread.Sleep(250);
        }
        return null;
    }

    private ChromeDriver GetDriver()
    {
        if (_driver is not null)
        {
            try { _ = _driver.WindowHandles; return _driver; }
            catch (WebDriverException) { CloseDriver(); }
        }
        Directory.CreateDirectory(_profileDirectory);
        var options = new ChromeOptions();
        options.AddArgument($"--user-data-dir={_profileDirectory}");
        options.AddArgument("--profile-directory=Default");
        _driver = new ChromeDriver(options);
        return _driver;
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() =>
            {
                CloseDriver();
                if (Directory.Exists(_profileDirectory)) Directory.Delete(_profileDirectory, recursive: true);
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private void CloseDriver()
    {
        var driver = _driver;
        _driver = null;
        if (driver is null) return;
        try { driver.Quit(); }
        catch (WebDriverException) { /* ignore */}
        finally { driver.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            CloseDriver();
        }
        finally { _gate.Release(); }
    }
}

public sealed record InstagramBatchProgress(string UserName, string Kind,
    InstagramSearchResult Result, int Completed, int Total);

public sealed record InstagramBatchRunResult(int Completed, int Total, string? StopReason);
