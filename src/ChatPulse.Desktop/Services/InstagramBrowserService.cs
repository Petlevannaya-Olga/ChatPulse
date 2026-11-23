using System.Text.Json;
using System.IO;
using System.Diagnostics;
using ChatPulse.Desktop.Settings;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Interactions;

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

    /// <summary>Confirms the active account using its own navigation link, not a cookie alone.</summary>
    public async Task<bool> IsLoggedInAsAsync(string userName,
        CancellationToken cancellationToken = default)
    {
        var expected = InstagramUserName.Normalize(userName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                var driver = GetDriver();
                driver.Navigate().GoToUrl("https://www.instagram.com/");
                if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null) return false;
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsCurrentInstagramAccount(driver, expected)) return true;
                    WaitWithCancellation(250, cancellationToken);
                }
                return ConfirmAccountThroughProfileNavigation(driver, expected, cancellationToken);
            }, cancellationToken);
        }
        catch (WebDriverException) { return false; }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Confirms whether the exact Instagram profile page is available. Returns null
    /// when Instagram does not provide reliable evidence (login, restriction, timeout).
    /// A successful browser navigation by itself does not prove that a profile exists.
    /// </summary>
    public async Task<bool?> ProfileOpensAsync(string profileUrl,
        CancellationToken cancellationToken = default)
    {
        var userName = InstagramActivityProfile.TryGetUserName(profileUrl)
            ?? throw new ArgumentException("Нужна корректная ссылка Instagram-профиля.", nameof(profileUrl));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                try
                {
                    var driver = GetDriver();
                    driver.Navigate().GoToUrl($"https://www.instagram.com/{userName}/");
                    if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null) return (bool?)null;
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var currentUrl = driver.Url;
                        var text = driver.FindElement(By.TagName("body")).Text;
                        if (InstagramProfilePageEvidence.IsExplicitlyUnavailable(text)) return false;
                        if (InstagramProfilePageEvidence.HasMatchingProfileHeading(userName, currentUrl,
                                driver.FindElements(By.CssSelector(
                                    "main header h1, main header h2, main header h3, " +
                                    "main header [role='heading'], main header span"))
                                    .Where(element => element.Displayed).Select(element => element.Text)))
                            return true;
                        WaitWithCancellation(350, cancellationToken);
                    }
                    return (bool?)null;
                }
                catch (OperationCanceledException) { throw; }
                catch (WebDriverException) { return (bool?)null; }
                catch (InvalidOperationException) { return (bool?)null; }
            }, cancellationToken);
        }
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

    /// <summary>
    /// Reads the profile follow button under the selected Chrome identity, without clicking it.
    /// A pending follow request, a missing control and any ambiguous DOM are inconclusive.
    /// </summary>
    public async Task<InstagramTaskCheckResult> CheckOwnFollowingAsync(
        string viewerUserName, string targetUserName, bool shouldFollow,
        CancellationToken cancellationToken = default)
    {
        var viewer = InstagramUserName.Normalize(viewerUserName);
        var target = InstagramUserName.Normalize(targetUserName);
        if (string.Equals(viewer, target, StringComparison.OrdinalIgnoreCase))
            return InstagramTaskCheckResult.Inconclusive("Нельзя проверять подписку аккаунта на самого себя.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                try
                {
                    var driver = GetDriver();
                    driver.Navigate().GoToUrl("https://www.instagram.com/");
                    if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null)
                        return InstagramTaskCheckResult.Inconclusive($"Сессия @{viewer} не авторизована в Chrome.");

                    var authenticated = false;
                    for (var attempt = 0; attempt < 18; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsCurrentInstagramAccount(driver, viewer))
                        {
                            authenticated = true;
                            break;
                        }
                        WaitWithCancellation(250, cancellationToken);
                    }
                    if (!authenticated)
                        authenticated = ConfirmAccountThroughProfileNavigation(driver, viewer, cancellationToken);
                    if (!authenticated)
                        return InstagramTaskCheckResult.Inconclusive(
                            $"Не удалось подтвердить аккаунт @{viewer}. Проверка не выполнена.");

                    driver.Navigate().GoToUrl($"https://www.instagram.com/{target}/");
                    string[] lastButtonLabels = [];
                    for (var attempt = 0; attempt < 24; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!string.Equals(InstagramActivityProfile.TryGetUserName(driver.Url), target,
                                StringComparison.OrdinalIgnoreCase))
                            return InstagramTaskCheckResult.Inconclusive("Instagram перенаправил на другую страницу.");
                        var labels = (IReadOnlyCollection<object>)((IJavaScriptExecutor)driver).ExecuteScript(
                            """
                            const nodes = [...document.querySelectorAll(
                              'main header button, main header [role="button"], ' +
                              '[role="main"] header button, [role="main"] header [role="button"]')];
                            const seen = new Set();
                            return nodes.filter(el => {
                              const box = el.getBoundingClientRect();
                              if (box.width === 0 || box.height === 0 || box.bottom <= 0 || box.top >= innerHeight ||
                                  getComputedStyle(el).visibility === 'hidden' || el.closest('a[href]') ||
                                  el.querySelector('a[href]')) return false;
                              const text = (el.innerText || el.getAttribute('aria-label') ||
                                  el.getAttribute('title') || '').trim().replace(/\s+/g, ' ');
                              if (!text || seen.has(text)) return false;
                              seen.add(text);
                              return true;
                            }).map(el => (el.innerText || el.getAttribute('aria-label') ||
                                el.getAttribute('title') || '').trim().replace(/\s+/g, ' '));
                            """);
                        var normalizedLabels = labels.Select(label => label?.ToString() ?? string.Empty).ToArray();
                        lastButtonLabels = normalizedLabels;
                        var state = InstagramFollowTaskRules.Classify(normalizedLabels);
                        if (state != InstagramFollowButtonState.UNKNOWN)
                        {
                            if (shouldFollow && state == InstagramFollowButtonState.FOLLOWING)
                                return InstagramTaskCheckResult.Confirmed($"Подписка @{viewer} на @{target} подтверждена.");
                            if (!shouldFollow && state == InstagramFollowButtonState.NOT_FOLLOWING)
                                return InstagramTaskCheckResult.Confirmed($"Отсутствие подписки @{viewer} на @{target} подтверждено.");
                            return InstagramTaskCheckResult.Inconclusive(shouldFollow
                                ? $"На странице @{target} отображается «Подписаться»: подписка не подтверждена."
                                : $"На странице @{target} отображается «Подписки»: отписка не подтверждена.");
                        }
                        WaitWithCancellation(350, cancellationToken);
                    }
                    var observed = lastButtonLabels.Length == 0 ? "нет видимых кнопок в заголовке"
                        : string.Join("; ", lastButtonLabels.Take(6)
                            .Select(label => label.Length > 50 ? label[..50] : label));
                    return InstagramTaskCheckResult.Inconclusive(
                        $"Не удалось однозначно определить кнопку подписки на странице @{target}. " +
                        $"Диагностика кнопок: {observed}.");
                }
                catch (OperationCanceledException) { throw; }
                catch (WebDriverException exception)
                {
                    return InstagramTaskCheckResult.Inconclusive($"Ошибка Instagram: {exception.Message}");
                }
                catch (InvalidOperationException exception)
                {
                    return InstagramTaskCheckResult.Inconclusive(exception.Message);
                }
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Best-effort browser verification of the user's own activity on a public Instagram post.
    /// Only exact observed usernames can produce CONFIRMED; scrolling limits, absent UI and
    /// missing results are INCONCLUSIVE. Browser selectors require live Instagram testing.
    /// </summary>
    public async Task<InstagramTaskVerificationResult> CheckTaskActivityAsync(
        string postUrl, string userName, bool checkLike, bool checkComment,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        string? postAuthorUserName = null, bool checkSave = false, bool checkRepost = false)
    {
        if (!checkLike && !checkComment && !checkSave && !checkRepost)
            throw new ArgumentException("Выберите хотя бы один вид проверки.");
        if (!InstagramTaskVerificationRules.TryGetPostUrl(postUrl, out var url))
            throw new ArgumentException("Нужна ссылка на публикацию или Reel Instagram.", nameof(postUrl));
        var user = InstagramUserName.Normalize(userName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                var driver = GetDriver();
                driver.Navigate().GoToUrl("https://www.instagram.com/");
                if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null)
                    throw new InvalidOperationException("В Chrome необходимо войти в Instagram.");
                // Session cookie alone does not identify the account. Read the
                // logged-in profile link in Instagram's navigation before trusting
                // any state that would cause a task to be hidden.
                var accountConfirmed = false;
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsCurrentInstagramAccount(driver, user))
                    {
                        accountConfirmed = true;
                        break;
                    }
                    WaitWithCancellation(250, cancellationToken);
                }
                if (!accountConfirmed)
                    accountConfirmed = ConfirmAccountThroughProfileNavigation(driver, user, cancellationToken);
                if (!accountConfirmed)
                {
                    var uncertain = InstagramTaskCheckResult.Inconclusive(
                        $"Не удалось подтвердить, что Chrome авторизован как @{user}. " +
                        "Откройте Instagram под основным аккаунтом и повторите проверку. " +
                        DescribeAccountNavigation(driver, user));
                    return new InstagramTaskVerificationResult(user, url,
                        checkLike ? uncertain : null, checkComment ? uncertain : null,
                        checkSave ? uncertain : null, checkRepost ? uncertain : null);
                }

                InstagramTaskCheckResult? like = null;
                InstagramTaskCheckResult? comment = null;
                InstagramTaskCheckResult? save = null;
                InstagramTaskCheckResult? repost = null;
                // All checks are read-only: never press a like, bookmark or repost
                // action just to determine whether it is active.
                if (checkLike || checkSave || checkRepost)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report("Считываем состояние действий на публикации…");
                    driver.Navigate().GoToUrl(url);
                    WaitFor(driver, () => driver.FindElements(By.CssSelector("article, main"))
                        .FirstOrDefault(element => element.Displayed), cancellationToken);
                    if (checkLike)
                    {
                        // Allow Instagram to render the action toolbar asynchronously.
                        // A visible SVG is not evidence until its state is readable.
                        for (var attempt = 0; attempt < 12; attempt++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            like = CheckOwnPostAction(driver, "like", user);
                            if (like.Status == InstagramTaskCheckStatus.CONFIRMED) break;
                            WaitWithCancellation(250, cancellationToken);
                        }
                    }
                    if (checkSave)
                    {
                        for (var attempt = 0; attempt < 12; attempt++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            save = CheckOwnPostAction(driver, "save", user);
                            if (save.Status == InstagramTaskCheckStatus.CONFIRMED) break;
                            WaitWithCancellation(250, cancellationToken);
                        }
                    }
                    if (checkRepost)
                    {
                        for (var attempt = 0; attempt < 12; attempt++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            repost = CheckOwnPostAction(driver, "repost", user);
                            if (repost.Status == InstagramTaskCheckStatus.CONFIRMED) break;
                            WaitWithCancellation(250, cancellationToken);
                        }
                    }
                }
                if (checkComment)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report("Ищем ваш комментарий под публикацией…");
                    comment = CheckDirectPostComment(driver, url, user, progress, cancellationToken);
                }
                return new InstagramTaskVerificationResult(user, url, like, comment, save, repost);
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static bool IsCurrentInstagramAccount(ChromeDriver driver, string userName)
    {
        try
        {
            // A post can link to any user's profile. Only the application's own
            // Profile navigation control is admissible evidence of the signed-in user.
            // Instagram does not always mark the sidebar as nav/aside.
            foreach (var link in driver.FindElements(By.CssSelector("a[href]")))
            {
                if (!InstagramTaskVerificationRules.IsExactProfileLink(
                        link.GetAttribute("href"), userName) || !link.Displayed)
                    continue;

                var inNavigation = (bool)((IJavaScriptExecutor)driver).ExecuteScript(
                    "return !!arguments[0].closest('nav, aside, [role=\"navigation\"]');", link);
                if (inNavigation) return true;

                var labels = new List<string>
                {
                    link.GetAttribute("aria-label") ?? string.Empty,
                    link.GetAttribute("title") ?? string.Empty,
                    link.Text
                };
                labels.AddRange(link.FindElements(By.CssSelector(
                    "svg[aria-label], [role='img'][aria-label], img[alt]"))
                    .Select(element => element.GetAttribute("aria-label") ??
                        element.GetAttribute("alt") ?? string.Empty));

                if (labels.Any(label => string.Equals(label.Trim(), "Profile",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(label.Trim(), "Профиль", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }
        catch (StaleElementReferenceException) { return false; }
        catch (WebDriverException) { return false; }
    }

    /// <summary>
    /// Opens only the Instagram navigation control labelled Profile. The resulting
    /// URL must match the expected username; arbitrary profile links in posts are
    /// not considered proof of the signed-in identity.
    /// </summary>
    private static bool ConfirmAccountThroughProfileNavigation(ChromeDriver driver,
        string expectedUserName, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profileControl = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const labels = new Set(['profile', 'профиль']);
                const visible = element => {
                    const r = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return r.width > 0 && r.height > 0 &&
                        style.visibility !== 'hidden' && style.display !== 'none';
                };
                const normalize = value => (value || '').trim().toLowerCase();
                const nodes = document.querySelectorAll(
                    'a, button, [role="button"], [aria-label], svg[aria-label], img[alt], span');
                for (const node of nodes) {
                    const labelsOnNode = [node.getAttribute('aria-label'),
                        node.getAttribute('title'), node.getAttribute('alt')];
                    // In some layouts Profile is the text of a nested span rather
                    // than the link or SVG accessibility label.
                    if (node.tagName.toLowerCase() === 'span')
                        labelsOnNode.push(node.textContent);
                    if (!labelsOnNode.some(value => labels.has(normalize(value)))) continue;
                    const control = node.closest('a, button, [role="button"]');
                    if (control && visible(control)) return control;
                }
                return null;
                """) as IWebElement;
            if (profileControl is null) return false;
            profileControl.Click();
            for (var attempt = 0; attempt < 24; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (InstagramTaskVerificationRules.IsExactProfileLink(
                        driver.Url, expectedUserName)) return true;
                var observed = InstagramActivityProfile.TryGetUserName(driver.Url);
                if (observed is not null && !string.Equals(observed, expectedUserName,
                        StringComparison.OrdinalIgnoreCase)) return false;
                WaitWithCancellation(250, cancellationToken);
            }
            return false;
        }
        catch (StaleElementReferenceException) { return false; }
        catch (WebDriverException) { return false; }
    }

    /// <summary>Read-only diagnostics; does not expose cookies or profile data.</summary>
    private static string DescribeAccountNavigation(ChromeDriver driver, string userName)
    {
        try
        {
            var info = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const expected = arguments[0].toLowerCase();
                const visible = node => {
                    const r = node.getBoundingClientRect();
                    const style = getComputedStyle(node);
                    return r.width > 0 && r.height > 0 &&
                        style.display !== 'none' && style.visibility !== 'hidden';
                };
                const anchors = [...document.querySelectorAll('a[href]')];
                const profileLinks = anchors.filter(a => {
                    try {
                        const u = new URL(a.href, location.href);
                        return u.hostname === 'www.instagram.com' &&
                            u.pathname.replace(/^\/+|\/+$/g, '').toLowerCase() === expected;
                    } catch { return false; }
                });
                const labels = new Set(['profile', 'профиль']);
                const controls = [...document.querySelectorAll(
                    'a, button, [role="button"], [aria-label], svg[aria-label], span')];
                const labelled = controls.filter(node => {
                    const raw = [node.getAttribute('aria-label'), node.getAttribute('title')];
                    if (node.tagName.toLowerCase() === 'span') raw.push(node.textContent);
                    return raw.some(x => labels.has((x || '').trim().toLowerCase()));
                });
                return 'совпадающих ссылок=' + profileLinks.length +
                    ', видимых=' + profileLinks.filter(visible).length +
                    ', элементов «Профиль»=' + labelled.length +
                    ', видимых кнопок=' + labelled.filter(n => {
                        const b = n.closest('a, button, [role="button"]');
                        return b && visible(b);
                    }).length;
                """, userName) as string;
            return "Диагностика меню: " + (info ?? "данные недоступны") + ".";
        }
        catch (WebDriverException)
        {
            return "Диагностика меню недоступна: страница изменилась.";
        }
    }

    private static InstagramTaskCheckResult CheckOwnPostAction(
        ChromeDriver driver, string action, string userName)
    {
        try
        {
            // Instagram may keep dozens of post toolbars in the DOM. Finding an
            // icon by aria-label on the entire document is not enough to prove
            // which publication it belongs to. Resolve a single post container
            // first, preferably using the shortcode from the requested URL.
            var labels = ((IJavaScriptExecutor)driver).ExecuteScript("""
                const action = arguments[0];
                const requestedUrl = arguments[1];
                const shortcodeOf = url => {
                    try {
                        const path = new URL(url, location.href).pathname;
                        const match = path.match(/^\/(?:p|reel|reels)\/([^/]+)\/?/i);
                        return match ? match[1].toLowerCase() : null;
                    } catch { return null; }
                };
                const wanted = shortcodeOf(requestedUrl);
                const visible = node => {
                    if (!node || !node.isConnected) return false;
                    const r = node.getBoundingClientRect();
                    if (r.width <= 0 || r.height <= 0) return false;
                    if (r.bottom <= 0 || r.top >= innerHeight ||
                        r.right <= 0 || r.left >= innerWidth) return false;
                    for (let el = node; el && el.nodeType === 1; el = el.parentElement) {
                        const style = getComputedStyle(el);
                        if (style.display === 'none' || style.visibility === 'hidden' ||
                            style.opacity === '0') return false;
                    }
                    return true;
                };
                const names = {
                    like: ['Like', 'Unlike', 'Нравится', 'Не нравится', 'Больше не нравится'],
                    save: ['Save', 'Unsave', 'Remove from saved', 'Сохранить', 'Удалить',
                           'Удалить из сохраненного', 'Удалить из сохранённого', 'Убрать из сохраненного', 'Убрать из сохранённого'],
                    repost: ['Repost', 'Remove repost', 'Undo repost', 'Репост',
                             'Сделать репост', 'Отменить репост', 'Удалить репост']
                };
                const accepted = new Set(names[action] || []);
                // On this Instagram layout the repost icon keeps the SAME accessible
                // label in both states. Distinguish the two supplied SVG paths, not
                // the aggregate repost count or the CSS classes.
                const observedState = svg => {
                    const label = svg.getAttribute('aria-label') || '';
                    const path = svg.querySelector('path')?.getAttribute('d') || '';
                    if (action === 'repost' && label === 'Сделать репост') {
                        if (path.startsWith('M16 6.001') && path.includes('-3.905 3.906'))
                            return '__REPOST_ACTIVE__';
                        if (path.startsWith('M19.998 9.497') && path.includes('-6.41-3.496'))
                            return '__REPOST_INACTIVE__';
                        return '__REPOST_UNKNOWN__';
                    }
                    if (action === 'save' && label === 'Удалить') {
                        // "Удалить" alone is not evidence: the icon must be the
                        // specific filled bookmark from the supplied markup.
                        if (svg.getAttribute('viewBox') === '0 0 24 24' &&
                            path.startsWith('M20 22') && path.includes('12 14.815'))
                            return '__SAVE_ACTIVE__';
                        return '__SAVE_UNKNOWN__';
                    }
                    return label;
                };
                const hasPermalink = container => [...container.querySelectorAll('a[href]')]
                    .some(a => shortcodeOf(a.href) === wanted);
                const articles = [...document.querySelectorAll('article')].filter(visible);
                const dialogs = [...document.querySelectorAll('[role="dialog"]')].filter(visible);
                // A permalink inside a post can identify its article, even when
                // Instagram renders multiple other articles in the background.
                const linkedArticles = wanted ? articles.filter(hasPermalink) : [];
                const linkedDialogs = wanted ? dialogs.filter(hasPermalink) : [];
                let root = null;
                if (linkedArticles.length === 1) root = linkedArticles[0];
                else if (!linkedArticles.length && linkedDialogs.length === 1)
                    root = linkedDialogs[0];
                else if (!linkedArticles.length && !linkedDialogs.length &&
                         articles.length === 1 && !dialogs.length &&
                         shortcodeOf(location.href) === wanted)
                    root = articles[0];
                else if (!linkedArticles.length && !linkedDialogs.length &&
                         dialogs.length === 1 && !articles.length &&
                         shortcodeOf(location.href) === wanted)
                    root = dialogs[0];
                if (!root && wanted &&
                    shortcodeOf(location.href) === wanted) {
                    // Direct post/Reels can omit article/dialog. Associate the action
                    // with one dominant visible media item, never with feed icons.
                    const rectOf = node => node.getBoundingClientRect();
                    const media = [...document.querySelectorAll('video, img')]
                        .filter(visible)
                        .map(node => ({ node, rect: rectOf(node) }))
                        .filter(item => item.rect.width >= 220 && item.rect.height >= 220 &&
                            item.rect.width * item.rect.height >= 65000 &&
                            !item.node.closest('nav, aside, header, [role="navigation"]'))
                        .sort((a, b) => b.rect.width * b.rect.height -
                            a.rect.width * a.rect.height);
                    const actionIcons = [...document.querySelectorAll('svg[aria-label]')]
                        .filter(svg => visible(svg) && accepted.has(svg.getAttribute('aria-label')) &&
                            !svg.closest('nav, aside, header, [role="navigation"]'));
                    if (media.length && actionIcons.length) {
                        const mainMedia = media[0].rect;
                        const cx = mainMedia.left + mainMedia.width / 2;
                        const cy = mainMedia.top + mainMedia.height / 2;
                        const distance = rect => {
                            const x = rect.left + rect.width / 2;
                            const y = rect.top + rect.height / 2;
                            return Math.hypot(x - cx, y - cy);
                        };
                        // A second similarly sized media is ambiguous: this might
                        // be a feed/carousel instead of the requested single post.
                        const otherBigMedia = media.slice(1).some(item =>
                            item.rect.width * item.rect.height >=
                            mainMedia.width * mainMedia.height * 0.85 &&
                            distance(item.rect) > 160);
                        if (!otherBigMedia) {
                            const candidates = actionIcons.map(svg => {
                                const r = rectOf(svg);
                                // Distance from the heart to the media rectangle.
                                const x = r.left + r.width / 2;
                                const y = r.top + r.height / 2;
                                const dx = Math.max(mainMedia.left - x, 0,
                                    x - mainMedia.right);
                                const dy = Math.max(mainMedia.top - y, 0,
                                    y - mainMedia.bottom);
                                return { svg, distance: Math.hypot(dx, dy) };
                            }).filter(item => item.distance <= 240)
                              .sort((a, b) => a.distance - b.distance);
                            if (candidates.length === 1 ||
                                (candidates.length > 1 &&
                                 candidates[1].distance - candidates[0].distance > 120)) {
                                return [observedState(candidates[0].svg)];
                            }
                        }
                    }
                    // The open post may contain two equally sized media elements
                    // (for example a Reel cover and its video). In that case the
                    // media-only heuristic is ambiguous. For save/repost, anchor to
                    // the *single visible post-sized like control* in the same
                    // action toolbar. Comment hearts are normally 16px and must
                    // never be used as the post anchor.
                    if (action === 'save' || action === 'repost') {
                        const mainHearts = [...document.querySelectorAll('svg[aria-label]')]
                            .filter(svg => visible(svg) &&
                                ['Нравится', 'Не нравится', 'Like', 'Unlike',
                                 'Больше не нравится'].includes(svg.getAttribute('aria-label')) &&
                                !svg.closest('nav, aside, header, [role="navigation"]'))
                            .filter(svg => {
                                const r = svg.getBoundingClientRect();
                                return r.width >= 22 && r.height >= 22;
                            });
                        if (mainHearts.length === 1 && actionIcons.length) {
                            const h = mainHearts[0].getBoundingClientRect();
                            const hx = h.left + h.width / 2;
                            const hy = h.top + h.height / 2;
                            const nearby = actionIcons.map(svg => {
                                const r = svg.getBoundingClientRect();
                                const dx = r.left + r.width / 2 - hx;
                                const dy = r.top + r.height / 2 - hy;
                                return { svg, distance: Math.hypot(dx, dy),
                                    dx: Math.abs(dx), dy: Math.abs(dy) };
                            }).filter(item => item.distance <= 300 &&
                                // Post controls form a horizontal row or a
                                // vertical rail in different Instagram layouts.
                                (item.dy <= 90 || item.dx <= 90))
                              .sort((a, b) => a.distance - b.distance);
                            // A small difference between the two nearest buttons
                            // cannot prove which one belongs to the current post.
                            if (nearby.length === 1 ||
                                (nearby.length > 1 &&
                                 nearby[1].distance - nearby[0].distance > 90))
                                return [observedState(nearby[0].svg)];
                        }
                    }
                    return ['__NO_SCOPE__|articles=' + articles.length +
                        ', dialogs=' + dialogs.length + ', visibleActions=' + actionIcons.length +
                        ', visibleMedia=' + media.length + ', scope=ambiguous'];
                }
                if (!root) return ['__NO_SCOPE__|articles=' + articles.length +
                    ', dialogs=' + dialogs.length + ', matchingArticles=' +
                    linkedArticles.length + ', matchingDialogs=' + linkedDialogs.length];
                return [...root.querySelectorAll('svg[aria-label]')]
                    .filter(svg => visible(svg) && accepted.has(svg.getAttribute('aria-label')))
                    .filter(svg => !svg.closest('nav, aside, header, [role="navigation"]'))
                    .map(observedState);
                """, action, driver.Url) as System.Collections.ObjectModel.ReadOnlyCollection<object>;
            var labelValues = labels?.OfType<string>().ToArray() ?? [];
            if (labelValues.Length == 1 && labelValues[0].StartsWith("__NO_SCOPE__|", StringComparison.Ordinal))
                return InstagramTaskCheckResult.Inconclusive(
                    "Не удалось однозначно определить контейнер проверяемой публикации: " +
                    labelValues[0]["__NO_SCOPE__|".Length..] + ".");
            var (active, inactive, title) = action switch
            {
                "like" => (new[] { "Unlike", "Больше не нравится", "Не нравится" },
                    new[] { "Like", "Нравится" }, "Лайк"),
                "save" => (new[] { "__SAVE_ACTIVE__", "Remove from saved", "Unsave", "Удалить из сохраненного", "Удалить из сохранённого", "Убрать из сохраненного", "Убрать из сохранённого" },
                    new[] { "Save", "Сохранить" }, "Сохранение"),
                "repost" => (new[] { "__REPOST_ACTIVE__", "Remove repost", "Undo repost", "Отменить репост", "Удалить репост" },
                    new[] { "__REPOST_INACTIVE__", "Repost", "Репост" }, "Репост"),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            // Multiple matching buttons can belong to distinct posts or overlays.
            // Require exactly one indicator: never hide a task on ambiguous DOM.
            if (labelValues.Length != 1)
                return InstagramTaskCheckResult.Inconclusive(
                    $"{title}: найдено индикаторов состояния: {labelValues.Length}. " +
                    "Невозможно однозначно определить состояние нужной публикации.");
            var observed = labelValues[0];
            if (active.Contains(observed, StringComparer.OrdinalIgnoreCase))
                return InstagramTaskCheckResult.Confirmed($"{title} @{userName} подтверждён по активному состоянию.");
            if (inactive.Contains(observed, StringComparer.OrdinalIgnoreCase))
                return InstagramTaskCheckResult.Inconclusive(
                    observed == "__REPOST_INACTIVE__"
                        ? "Репост: обнаружена неактивная иконка. Репост не подтверждён."
                        : $"{title}: на публикации доступно действие «{observed}», но оно не подтверждено.");
            return InstagramTaskCheckResult.Inconclusive(
                $"{title}: Instagram не показал однозначный индикатор состояния.");
        }
        catch (WebDriverException exception)
        {
            return InstagramTaskCheckResult.Inconclusive(
                $"{action}: ошибка чтения состояния Instagram: {exception.Message}");
        }
    }

    /// <summary>
    /// Diagnostic-only probe: attempts to open the likes list without scrolling it,
    /// inspecting account activity or modifying the task/reaction.
    /// </summary>
    public async Task<string> InspectPostLikesDialogAsync(string postUrl,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!InstagramTaskVerificationRules.TryGetPostUrl(postUrl, out var url))
            throw new ArgumentException("Нужна ссылка на публикацию или Reel Instagram.", nameof(postUrl));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                var driver = GetDriver();
                driver.Navigate().GoToUrl("https://www.instagram.com/");
                if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null)
                    return "Нет активной сессии Instagram в Chrome ChatPulse.";
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Открываем публикацию Instagram…");
                driver.Navigate().GoToUrl(url);

                var trigger = WaitFor(driver, () => FindPostLikesTrigger(driver), cancellationToken);
                if (trigger is null)
                    return "Счётчик лайков не найден. " + DescribeLikesTriggerState(driver);

                var existingDialogs = GetVisibleLikesDialogCandidates(driver).ToArray();
                progress?.Report("Счётчик найден, пытаемся открыть список лайков…");
                if (!TryOpenPostLikes(driver, existingDialogs, cancellationToken, out var clickError))
                    return clickError + " " + DescribeLikesTriggerState(driver);

                // Do not confuse an unrelated existing dialog with a likes list.
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dialog = FindPostLikesDialog(driver, existingDialogs);
                    if (dialog is not null)
                    {
                        var profiles = dialog.FindElements(By.CssSelector("a[href]"))
                            .Count(link => IsInstagramProfileHref(link.GetAttribute("href")));
                        return $"Список лайков открылся. Видимых ссылок на профили: {profiles}. " +
                            "Прокрутка не выполнялась, отметка задания не менялась.";
                    }
                    WaitWithCancellation(250, cancellationToken);
                }
                return "После нажатия список лайков не подтверждён. " +
                    DescribeLikesTriggerState(driver);
            }, cancellationToken);
        }
        catch (WebDriverException exception)
        {
            return $"Ошибка браузера Instagram при открытии списка: {exception.Message}";
        }
        finally { _gate.Release(); }
    }

    private static string DescribeLikesTriggerState(ChromeDriver driver)
    {
        // Only aggregate diagnostics; do not include account names or page HTML.
        var candidates = driver.FindElements(By.CssSelector(
            "main a[href*='liked_by'], main button, main [role='button'], article button"));
        var labelled = candidates.Count(element =>
        {
            try
            {
                return element.Displayed && InstagramTaskVerificationRules.IsLikesCounterLabel(
                    element.Text + " " + element.GetAttribute("aria-label"));
            }
            catch (StaleElementReferenceException) { return false; }
        });
        return $"Видимых кандидатов со счётчиком лайков: {labelled}; " +
            $"всего исследованных элементов: {candidates.Count}.";
    }

    private static InstagramTaskCheckResult CheckPostLike(ChromeDriver driver, string url,
        string user, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            driver.Navigate().GoToUrl(url);
            var existingDialogs = GetVisibleLikesDialogCandidates(driver).ToArray();
            var trigger = WaitFor(driver, () => FindPostLikesTrigger(driver), cancellationToken);
            if (trigger is null)
                return InstagramTaskCheckResult.Inconclusive(
                    "Instagram не показал доступную ссылку на список лайков.");
            if (!TryOpenPostLikes(driver, existingDialogs, cancellationToken, out var clickError))
                return InstagramTaskCheckResult.Inconclusive(clickError);
            var dialog = WaitFor(driver, () => FindPostLikesDialog(driver, existingDialogs), cancellationToken);
            if (dialog is null)
                return InstagramTaskCheckResult.Inconclusive(
                    "После нажатия список лайков не подтверждён: Instagram мог изменить разметку или ограничить доступ.");
            for (var scroll = 0; scroll <= InstagramTaskVerificationRules.MAX_LIKE_SCROLLS; scroll++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Instagram replaces list nodes during lazy loading. Never reuse
                // an IWebElement acquired before the previous scroll.
                dialog = FindPostLikesDialog(driver, existingDialogs);
                if (dialog is null)
                    return InstagramTaskCheckResult.Inconclusive(
                        "Окно лайков исчезло или перерисовывается во время проверки.");
                try
                {
                    if (HasExactUserLink(dialog, user))
                        return InstagramTaskCheckResult.Confirmed($"Лайк @{user} найден.");
                }
                catch (StaleElementReferenceException)
                {
                    // The next iteration must locate the fresh dialog.
                    dialog = FindPostLikesDialog(driver, existingDialogs);
                    if (dialog is null)
                        return InstagramTaskCheckResult.Inconclusive(
                            "Instagram обновил окно лайков, и оно больше не найдено.");
                }
                if (scroll == InstagramTaskVerificationRules.MAX_LIKE_SCROLLS) break;
                progress?.Report($"Лайки: прокрутка {scroll + 1} из 10…");
                if (!ScrollInstagramDialog(driver, existingDialogs))
                    return InstagramTaskCheckResult.Inconclusive(
                        "Не удалось прокрутить список лайков: контейнер не изменил позицию после JavaScript и прокрутки мышью. Возможно, список уже в конце или Instagram изменил его разметку.");
                WaitWithCancellation(1400, cancellationToken);
            }
            return InstagramTaskCheckResult.Inconclusive(
                "После 10 прокруток лайк не обнаружен. Полнота списка не подтверждена.");
        }
        catch (OperationCanceledException) { throw; }
        catch (WebDriverException exception)
        {
            return InstagramTaskCheckResult.Inconclusive($"Ошибка Instagram при проверке лайков: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return InstagramTaskCheckResult.Inconclusive($"Не удалось проверить лайки: {exception.Message}");
        }
    }

    /// <summary>
    /// Check comments directly on the target publication. This avoids assuming the
    /// publication must appear in the author's profile grid. Absence after a partial
    /// list is inconclusive, never a negative result.
    /// </summary>
    /// <summary>
    /// Read-only search for the signed-in user's comment on an opened post/Reel.
    /// Never equate an incomplete list with absence of a comment.
    /// </summary>
    private static InstagramTaskCheckResult CheckDirectPostComment(
        ChromeDriver driver, string url, string user, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            driver.Navigate().GoToUrl(url);
            var shortcode = GetInstagramPublicationShortcode(url);
            if (string.IsNullOrWhiteSpace(shortcode))
                return InstagramTaskCheckResult.Inconclusive("Не удалось определить публикацию для проверки комментариев.");

            // A direct post/Reel URL does not necessarily open its comments panel.
            // First open the comment control, then look for comments and scroll.
            var openingError = EnsureDirectPostCommentsOpened(driver, shortcode, cancellationToken);
            if (openingError is not null)
                return InstagramTaskCheckResult.Inconclusive(openingError);

            // Instagram's comment rows are divs, not necessarily ul/li. Their time links
            // contain /p/<shortcode>/c/<comment-id>/ even when the post was opened as a Reel.
            // Observe each DOM iteration atomically: React may replace the entire list.
            var stalled = 0;
            string? previousSignature = null;
            for (var batch = 0; batch < 100; batch++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = ((IJavaScriptExecutor)driver).ExecuteScript("""
                    const shortcode = arguments[0];
                    const expected = arguments[1].toLowerCase();
                    const visible = el => {
                        if (!el?.isConnected) return false;
                        const r = el.getBoundingClientRect();
                        const style = getComputedStyle(el);
                        return r.width > 0 && r.height > 0 &&
                            style.display !== 'none' && style.visibility !== 'hidden';
                    };
                    const path = el => {
                        try { return new URL(el.href, location.href).pathname; }
                        catch { return ''; }
                    };
                    const commentPath = new RegExp('^/(?:p|reel)/' +
                        shortcode.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '/c/[^/]+/?$', 'i');
                    const allCommentLinks = [...document.querySelectorAll('a[href]')]
                        .filter(a => visible(a) && /^\/(?:p|reel)\/[^/]+\/c\/[^/]+\/?$/i.test(path(a)));
                    const links = allCommentLinks.filter(a => commentPath.test(path(a)));
                    const profilePath = '/' + expected + '/';
                    const isProfile = a => path(a).toLowerCase() === profilePath;
                    // A user profile elsewhere on the page cannot confirm a comment.
                    const found = links.some(link => {
                        let row = link.parentElement;
                        for (let depth = 0; row && depth < 12; depth++, row = row.parentElement) {
                            const ownLinks = [...row.querySelectorAll('a[href]')]
                                .filter(a => commentPath.test(path(a)));
                            if (ownLinks.length > 1) break;
                            if ([...row.querySelectorAll('a[href]')].some(isProfile)) return true;
                        }
                        return false;
                    });
                    if (found) return [true, links.length, 'found', links.at(-1)?.href || '', allCommentLinks.length];

                    const heading = [...document.querySelectorAll('span, h1, h2, h3')]
                        .find(el => visible(el) && /^(Комментарии|Comments)$/i.test(
                            (el.textContent || '').trim()));
                    // On the supplied Instagram DOM the comments panel is a div,
                    // not an article/dialog, and comment links may not match the
                    // requested shortcode if Instagram redirected to another post.
                    // Never treat those links as positive matches.
                    const first = links[0] || allCommentLinks[0];
                    let panel = null;
                    if (heading) {
                        for (let el = heading.parentElement; el && el !== document.body;
                             el = el.parentElement) {
                            if (el.querySelectorAll('a[href*="/c/"]').length > 0) {
                                panel = el;
                                break;
                            }
                        }
                    }
                    const root = panel || first?.parentElement?.parentElement || heading?.parentElement;
                    if (!root) return [false, 0, 'loading', '', allCommentLinks.length];

                    let scrolling = null;
                    const anchors = [first, root, heading].filter(Boolean);
                    for (const anchor of anchors) {
                        for (let el = anchor; el && el !== document.body; el = el.parentElement) {
                            if (!visible(el)) continue;
                            if (el.scrollHeight > el.clientHeight + 12 && el.clientHeight > 90) {
                                if (!scrolling || (el.clientHeight < scrolling.clientHeight))
                                    scrolling = el;
                            }
                        }
                    }
                    const expand = [...(panel || root).querySelectorAll('button, [role="button"]')]
                        .find(el => visible(el) &&
                            /^(?:смотреть все ответы|посмотреть ответы|показать ответы|view (?:all )?replies|view more replies|view more comments|load more comments|показать ещё комментарии|показать еще комментарии)/i
                                .test((el.textContent || el.getAttribute('aria-label') || '').trim()));
                    if (expand) {
                        expand.click();
                        return [false, links.length, 'expanded', links.at(-1)?.href || '', allCommentLinks.length];
                    }
                    if (scrolling) {
                        const before = scrolling.scrollTop;
                        scrolling.scrollTop = Math.min(scrolling.scrollHeight,
                            before + Math.max(200, Math.floor(scrolling.clientHeight * 0.8)));
                        if (scrolling.scrollTop > before + 1)
                            return [false, links.length, 'scrolled', links.at(-1)?.href || '', allCommentLinks.length, scrolling.scrollTop];
                    }
                    return [false, links.length, (first || heading) ? 'wheel' : 'loading', links.at(-1)?.href || '', allCommentLinks.length];
                    """, shortcode, user) as System.Collections.ObjectModel.ReadOnlyCollection<object>;

                if (snapshot is null || snapshot.Count < 3)
                    return InstagramTaskCheckResult.Inconclusive(
                        "Не удалось прочитать структуру комментариев Instagram.");
                if (Convert.ToBoolean(snapshot[0]))
                    return InstagramTaskCheckResult.Confirmed($"Комментарий @{user} найден.");

                var count = Convert.ToInt32(snapshot[1]);
                var action = Convert.ToString(snapshot[2]);
                var visibleComments = snapshot.Count > 4 ? Convert.ToInt32(snapshot[4]) : count;
                var position = snapshot.Count > 5 ? Convert.ToString(snapshot[5]) : string.Empty;
                var signature = $"{count}:{visibleComments}:{position}:{(snapshot.Count > 3 ? snapshot[3] : string.Empty)}";
                if (action == "wheel")
                {
                    // If setting scrollTop did not move a virtualized list, send a
                    // real wheel gesture over a fresh comment anchor, not the page.
                    var originElement = ((IJavaScriptExecutor)driver).ExecuteScript("""
                        const code = arguments[0];
                        const commentLinks = [...document.querySelectorAll('a[href]')]
                            .filter(a => {
                                try { return /^\/(?:p|reel)\/[^/]+\/c\/[^/]+\/?$/i.test(new URL(a.href).pathname); }
                                catch { return false; }
                            });
                        return commentLinks.find(a => {
                            try { return new URL(a.href).pathname.includes('/' + code + '/c/'); }
                            catch { return false; }
                        }) || commentLinks[0] || [...document.querySelectorAll('span, h1, h2, h3')]
                            .find(el => /^(Комментарии|Comments)$/i.test((el.textContent || '').trim())) || null;
                        """, shortcode) as IWebElement;
                    if (originElement is not null)
                    {
                        try
                        {
                            var origin = new WheelInputDevice.ScrollOrigin { Element = originElement };
                            new Actions(driver).ScrollFromOrigin(origin, 0, 450).Perform();
                            action = "wheel-scrolled";
                        }
                        catch (StaleElementReferenceException) { action = "loading"; }
                        catch (WebDriverException) { action = "loading"; }
                    }
                }
                progress?.Report($"Комментарии: для публикации {count}, всего видимых {visibleComments}, шаг {batch + 1}…");
                WaitWithCancellation(action == "loading" ? 450 : 850, cancellationToken);
                stalled = signature == previousSignature ? stalled + 1 : 0;
                if (stalled >= 8)
                    return InstagramTaskCheckResult.Inconclusive(
                        $"Комментарии не удалось подгрузить дальше (для публикации {count}, " +
                        $"всего видимых {visibleComments}); полнота списка не подтверждена. " +
                        (count == 0 && visibleComments > 0
                            ? "Ссылки комментариев относятся к другому shortcode — проверьте адрес публикации."
                            : ""));
                previousSignature = signature;
            }
            return InstagramTaskCheckResult.Inconclusive(
                "Достигнут предел прокрутки комментариев; полнота списка неизвестна.");
        }
        catch (OperationCanceledException) { throw; }
        catch (WebDriverException exception)
        {
            return InstagramTaskCheckResult.Inconclusive(
                $"Ошибка Instagram при проверке комментария: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return InstagramTaskCheckResult.Inconclusive(
                $"Не удалось проверить комментарий: {exception.Message}");
        }
    }

    /// <summary>
    /// Open the comments area of a directly navigated post/Reel before inspecting rows.
    /// We only click a uniquely identified comment control, never a reaction button.
    /// The absence of a visible panel is inconclusive rather than a missing comment.
    /// </summary>
    private static string? EnsureDirectPostCommentsOpened(
        ChromeDriver driver, string expectedShortcode, CancellationToken cancellationToken)
    {
        var executor = (IJavaScriptExecutor)driver;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = executor.ExecuteScript("""
                const visible = e => {
                    if (!e?.isConnected) return false;
                    const r = e.getBoundingClientRect();
                    const s = getComputedStyle(e);
                    return r.width > 0 && r.height > 0 &&
                        s.display !== 'none' && s.visibility !== 'hidden';
                };
                const commentLink = a => {
                    try { return /^\/(?:p|reel)\/[^/]+\/c\/[^/]+\/?$/i
                        .test(new URL(a.href, location.href).pathname); }
                    catch { return false; }
                };
                const hasRows = [...document.querySelectorAll('a[href]')]
                    .some(a => visible(a) && commentLink(a));
                const hasHeading = [...document.querySelectorAll('span, h1, h2, h3')]
                    .some(e => visible(e) && /^(Комментарии|Comments)$/i.test((e.textContent || '').trim()));
                return [hasRows, hasHeading];
                """) as System.Collections.ObjectModel.ReadOnlyCollection<object>;
            if (state is not null && state.Count >= 2 &&
                (Convert.ToBoolean(state[0]) || Convert.ToBoolean(state[1])))
                return null;
            WaitWithCancellation(200, cancellationToken);
        }

        // A direct post can coexist with many cached feed toolbars. Only consider
        // controls actually inside the viewport, and tie the comment button to
        // the target post's 24px like control near its dominant media.
        var candidates = executor.ExecuteScript("""
            const visible = e => {
                if (!e?.isConnected) return false;
                const r = e.getBoundingClientRect();
                if (r.width < 2 || r.height < 2 || r.right <= 0 || r.bottom <= 0 ||
                    r.left >= innerWidth || r.top >= innerHeight) return false;
                for (let a = e; a && a.nodeType === 1; a = a.parentElement) {
                    const s = getComputedStyle(a);
                    if (s.display === 'none' || s.visibility === 'hidden' || s.opacity === '0')
                        return false;
                }
                return true;
            };
            const label = e => (e.getAttribute('aria-label') ||
                e.querySelector('title')?.textContent || '').trim();
            const commentName = /^(?:комментировать|комментарии|комментарий|comment|comments|view comments)$/i;
            const likeName = /^(?:нравится|не нравится|больше не нравится|like|unlike)$/i;
            const iconButtons = predicate => [...new Set(
                [...document.querySelectorAll('svg[aria-label]')]
                    .filter(e => visible(e) && predicate(label(e)))
                    .map(e => e.closest('[role="button"], button, a'))
                    .filter(e => e && visible(e))
            )];
            const comments = iconButtons(v => commentName.test(v));
            const numeric = e => /^\d[\d\s.,]*[KkMm]?$/u.test(
                (e.innerText || '').trim().replace(/\s+/g, ' '));
            const numbered = comments.filter(numeric);
            const expectedCode = arguments[0];
            const currentPost = location.pathname.match(/^\/(?:p|reel|reels|tv)\/([^/]+)(?:\/|$)/i);
            const currentMatches = currentPost &&
                currentPost[1].toLowerCase() === expectedCode.toLowerCase();
            const matchingLinks = [...document.querySelectorAll('a[href]')].filter(a => {
                if (!visible(a)) return false;
                try {
                    const m = new URL(a.href, location.href).pathname
                        .match(/^\/(?:p|reel|reels|tv)\/([^/]+)(?:\/|$)/i);
                    return m && m[1].toLowerCase() === expectedCode.toLowerCase();
                } catch { return false; }
            });
            // Instagram can open a post in an overlay while leaving the feed URL.
            // Do not click a feed control unless the target post is identifiable.
            if (!currentMatches && !matchingLinks.length)
                return [null, comments.length, numbered.length,
                    'post-unverified; path=' + location.pathname + '; targetLinks=0'];
            if (currentPost && !currentMatches)
                return [null, comments.length, numbered.length,
                    'wrong-post; path=' + location.pathname];
            const media = [...document.querySelectorAll('video, img')]
                .filter(visible)
                .map(e => ({e, r:e.getBoundingClientRect()}))
                .filter(o => o.r.width >= 220 && o.r.height >= 220 &&
                    o.r.width * o.r.height >= 65000 &&
                    !o.e.closest('nav, aside, header, [role="navigation"]'))
                .sort((a,b) => b.r.width*b.r.height - a.r.width*a.r.height);
            if (!media.length) return [null, comments.length, numbered.length, 'no-main-media'];
            const m = media[0].r;
            if (!currentMatches) {
                const aroundMedia = matchingLinks.some(a => {
                    const r = a.getBoundingClientRect();
                    const x = r.left + r.width/2, y = r.top + r.height/2;
                    return Math.hypot(Math.max(m.left-x, 0, x-m.right),
                                      Math.max(m.top-y, 0, y-m.bottom)) <= 150;
                });
                if (!aroundMedia) return [null, comments.length, numbered.length,
                    'overlay-post-not-linked-to-media; path=' + location.pathname +
                    '; targetLinks=' + matchingLinks.length];
            }
            const mcx = m.left + m.width/2, mcy = m.top + m.height/2;
            const other = media.slice(1).some(o =>
                o.r.width*o.r.height >= m.width*m.height*0.85 &&
                Math.hypot(o.r.left+o.r.width/2-mcx, o.r.top+o.r.height/2-mcy) > 160);
            if (other) return [null, comments.length, numbered.length, 'ambiguous-media'];
            const edgeDistance = e => {
                const r = e.getBoundingClientRect();
                const x=r.left+r.width/2, y=r.top+r.height/2;
                return Math.hypot(Math.max(m.left-x, 0, x-m.right),
                                  Math.max(m.top-y, 0, y-m.bottom));
            };
            // Only the 24px post heart, not the 16px hearts on comment rows.
            const hearts = [...document.querySelectorAll('svg[aria-label]')]
                .filter(e => visible(e) && likeName.test(label(e)) &&
                    e.getBoundingClientRect().height >= 20 && edgeDistance(e) <= 240)
                .sort((a,b) => edgeDistance(a)-edgeDistance(b));
            if (!hearts.length || (hearts.length > 1 &&
                edgeDistance(hearts[1]) - edgeDistance(hearts[0]) <= 120))
                return [null, comments.length, numbered.length, 'ambiguous-heart'];
            const hr = hearts[0].getBoundingClientRect();
            const hx = hr.left+hr.width/2, hy = hr.top+hr.height/2;
            const paired = numbered.filter(button => {
                const r = button.getBoundingClientRect();
                const x=r.left+r.width/2, y=r.top+r.height/2;
                return Math.abs(y-hy) <= 55 && x > hx-30 && x < hx+240 &&
                    edgeDistance(button) <= 260;
            });
            // The comment count can be displayed on a different row from the
            // post heart. When the address bar identifies the exact requested
            // post and only one comment-count control is actually on screen,
            // do not reject it solely because the icons are not aligned.
            // For overlays and multiple controls, keep the stricter pairing.
            if (paired.length === 0 && currentMatches &&
                comments.length === 1 && numbered.length === 1 &&
                edgeDistance(numbered[0]) <= 420) {
                return [numbered[0], comments.length, numbered.length,
                    'single-visible-on-target-post'];
            }
            return [paired.length === 1 ? paired[0] : null,
                comments.length, numbered.length,
                paired.length === 1 ? 'paired' : 'paired=' + paired.length];
            """, expectedShortcode) as System.Collections.ObjectModel.ReadOnlyCollection<object>;
        if (candidates is null || candidates.Count < 3)
            return "Не удалось прочитать кнопки комментариев Instagram.";

        var totalButtons = Convert.ToInt32(candidates[1]);
        var numberedButtons = Convert.ToInt32(candidates[2]);
        if (candidates[0] is not IWebElement button)
            return "Не удалось однозначно определить кнопку открытия комментариев " +
                $"(кнопок на экране: {totalButtons}, со счётчиком: {numberedButtons}, " +
                $"выбор: {(candidates.Count > 3 ? candidates[3] : "неизвестно")}). " +
                "Проверка не завершена.";
        try
        {
            new Actions(driver).MoveToElement(button).Click().Perform();
        }
        catch (StaleElementReferenceException)
        {
            return "Instagram обновил кнопку комментариев до нажатия; повторите проверку.";
        }
        catch (WebDriverException exception)
        {
            return $"Не удалось открыть комментарии: {exception.Message}";
        }

        // Do not report success merely because the click did not throw.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var opened = executor.ExecuteScript("""
                const visible = e => {
                    const r = e.getBoundingClientRect();
                    return e.isConnected && r.width > 0 && r.height > 0;
                };
                const heading = [...document.querySelectorAll('span, h1, h2, h3')]
                    .some(e => visible(e) && /^(Комментарии|Comments)$/i.test((e.textContent || '').trim()));
                const rows = [...document.querySelectorAll('a[href]')].some(a => {
                    if (!visible(a)) return false;
                    try { return /^\/(?:p|reel)\/[^/]+\/c\/[^/]+\/?$/i
                        .test(new URL(a.href, location.href).pathname); }
                    catch { return false; }
                });
                return heading || rows;
                """);
            if (opened is true) return null;
            WaitWithCancellation(300, cancellationToken);
        }
        return "Кнопка комментариев нажата, но список не открылся; полнота проверки неизвестна.";
    }

    private static string? GetInstagramPublicationShortcode(string? rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Host != "instagram.com" && !uri.Host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase)))
            return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
            (segments[0].Equals("p", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("reel", StringComparison.OrdinalIgnoreCase))
            ? segments[1]
            : null;
    }

    private static InstagramTaskCheckResult CheckPostComment(ChromeDriver driver, string url,
        string user, string? postAuthorUserName, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            driver.Navigate().GoToUrl(url);
            string? authorUrl = null;
            if (!string.IsNullOrWhiteSpace(postAuthorUserName))
            {
                // The explicit author is a user-provided hint, never inferred from commenters.
                // Normalize before constructing the URL to avoid navigation to arbitrary hosts.
                var author = InstagramUserName.Normalize(postAuthorUserName.Trim().TrimStart('@'));
                authorUrl = $"https://www.instagram.com/{author}/";
            }
            else
            {
                // Reels may expose the author outside <article><header>.
                var authorLink = WaitFor(driver, () =>
                    driver.FindElements(By.CssSelector(
                        "article header a[href], header a[href], main header a[href]"))
                    .FirstOrDefault(element => element.Displayed &&
                        IsInstagramProfileHref(element.GetAttribute("href"))), cancellationToken);
                authorUrl = authorLink?.GetAttribute("href");
            }
            if (string.IsNullOrWhiteSpace(authorUrl))
                return InstagramTaskCheckResult.Inconclusive(
                    "Не удалось определить автора публикации. Укажите его username в окне проверки.");
            driver.Navigate().GoToUrl(authorUrl);
            var targetShortcode = GetInstagramPublicationShortcode(url);
            if (targetShortcode is null)
                return InstagramTaskCheckResult.Inconclusive(
                    "Не удалось прочитать идентификатор проверяемой публикации.");

            // Wait for the profile grid before scrolling. Otherwise the first post can
            // be skipped while Instagram is still rendering the initial grid.
            var gridReady = WaitFor(driver, () =>
                driver.FindElements(By.CssSelector("main a[href*='/p/'], main a[href*='/reel/']"))
                    .FirstOrDefault(), cancellationToken);
            if (gridReady is null)
                return InstagramTaskCheckResult.Inconclusive(
                    "В профиле автора не загрузились ссылки на публикации.");

            // Instagram renders profile tiles asynchronously. Read all hrefs as one DOM
            // snapshot before moving the page: fast element-by-element Selenium calls
            // may outlive a render and invalidate references.
            IWebElement? postLink = null;
            var inspectedLinks = 0;
            var lastScrollY = -1d;
            for (var page = 0; page < 60; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Give the current row time to render before inspecting and scrolling.
                WaitWithCancellation(page == 0 ? 1500 : 1100, cancellationToken);
                string[] hrefs;
                try
                {
                    hrefs = ((IJavaScriptExecutor)driver).ExecuteScript("""
                        return Array.from(document.querySelectorAll(
                            "main a[href*='/p/'], main a[href*='/reel/']"))
                            .map(a => a.href);
                        """) is System.Collections.ObjectModel.ReadOnlyCollection<object> values
                        ? values.OfType<string>().ToArray()
                        : [];
                }
                catch (StaleElementReferenceException)
                {
                    // A concurrent Instagram rerender: retry at the same position.
                    continue;
                }

                inspectedLinks += hrefs.Length;
                if (hrefs.Any(href => string.Equals(GetInstagramPublicationShortcode(href),
                        targetShortcode, StringComparison.OrdinalIgnoreCase)))
                {
                    // Resolve a fresh element only after matching its URL. Never keep
                    // IWebElement references across scrolling/re-rendering.
                    try
                    {
                        postLink = driver.FindElements(By.CssSelector(
                                "main a[href*='/p/'], main a[href*='/reel/']"))
                            .FirstOrDefault(link => string.Equals(
                                GetInstagramPublicationShortcode(link.GetAttribute("href")),
                                targetShortcode, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (StaleElementReferenceException) { /* resolve again next pass */ }
                    if (postLink is not null) break;
                    continue;
                }

                // Move less than a row at a time, so newly loaded top tiles cannot
                // be skipped. A scroll is only done AFTER the current links are read.
                var scrollY = Convert.ToDouble(((IJavaScriptExecutor)driver).ExecuteScript(
                    "return window.scrollY;"));
                var nextY = Convert.ToDouble(((IJavaScriptExecutor)driver).ExecuteScript(
                    "window.scrollBy(0, 240); return window.scrollY;"));
                if (Math.Abs(nextY - scrollY) < 1 && Math.Abs(scrollY - lastScrollY) < 1)
                    break;
                lastScrollY = scrollY;
            }
            if (postLink is null)
                return InstagramTaskCheckResult.Inconclusive(
                    $"Публикация {targetShortcode} не найдена в загруженной сетке профиля автора " +
                    $"(проверено ссылок: {inspectedLinks}).");
            postLink.Click();
            var surface = WaitFor(driver, () => FindListDialog(driver) ??
                driver.FindElements(By.CssSelector("article")).FirstOrDefault(element => element.Displayed),
                cancellationToken);
            if (surface is null)
                return InstagramTaskCheckResult.Inconclusive("Не удалось открыть комментарии публикации.");

            var previousCount = -1;
            var stalled = 0;
            for (var batch = 0; batch < 250; batch++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Check author anchors only within comment list items, not the post header,
                // likes, suggested users or author information.
                var entries = surface.FindElements(By.CssSelector("ul li"));
                if (entries.Any(entry => entry.FindElements(By.CssSelector("a[href]"))
                        .Any(link => InstagramTaskVerificationRules.IsExactProfileLink(
                            link.GetAttribute("href"), user))))
                    return InstagramTaskCheckResult.Confirmed($"Комментарий @{user} найден.");

                var loadMore = surface.FindElements(By.CssSelector("button, [role='button']"))
                    .FirstOrDefault(button => button.Displayed && IsMoreCommentsButton(button));
                if (loadMore is null)
                    return InstagramTaskCheckResult.Inconclusive(
                        "Кнопка подгрузки комментариев недоступна; полнота списка неизвестна.");
                progress?.Report($"Подгружаем комментарии: шаг {batch + 1}…");
                loadMore.Click();
                WaitWithCancellation(900, cancellationToken);
                var count = surface.FindElements(By.CssSelector("ul li")).Count;
                stalled = count == previousCount ? stalled + 1 : 0;
                if (stalled >= 4)
                    return InstagramTaskCheckResult.Inconclusive(
                        "Instagram перестал загружать новые комментарии.");
                previousCount = count;
            }
            return InstagramTaskCheckResult.Inconclusive(
                "Достигнут технический предел подгрузки комментариев.");
        }
        catch (OperationCanceledException) { throw; }
        catch (WebDriverException exception)
        {
            return InstagramTaskCheckResult.Inconclusive(
                $"Ошибка Instagram при проверке комментариев: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return InstagramTaskCheckResult.Inconclusive(
                $"Не удалось проверить комментарии: {exception.Message}");
        }
    }

    // Like checking must not use the generic dialog finder: the comments/login
    // dialog can already be visible when the publication is opened.
    private static IEnumerable<IWebElement> GetVisibleLikesDialogCandidates(ChromeDriver driver)
        => driver.FindElements(By.CssSelector("[role='dialog'], [aria-modal='true']"))
            .Where(element => element.Displayed);

    private static IWebElement? FindPostLikesDialog(ChromeDriver driver,
        IReadOnlyCollection<IWebElement> existingDialogs)
    {
        foreach (var dialog in GetVisibleLikesDialogCandidates(driver))
        {
            try
            {
                if (dialog.FindElements(By.CssSelector("h1,h2,h3,[role='heading']"))
                    .Any(heading => InstagramTaskVerificationRules.IsLikesDialogHeading(heading.Text)))
                    return dialog;

                // Some Instagram layouts omit a heading. Accept a newly opened
                // profile list only when it has no explicit unrelated heading.
                var headings = dialog.FindElements(By.CssSelector("h1,h2,h3,[role='heading']"))
                    .Where(heading => heading.Displayed)
                    .Select(heading => heading.Text.Trim())
                    .Where(heading => heading.Length > 0).ToArray();
                if (!existingDialogs.Contains(dialog) && headings.Length == 0 &&
                    dialog.FindElements(By.CssSelector("a[href]"))
                        .Any(link => IsInstagramProfileHref(link.GetAttribute("href"))))
                    return dialog;
            }
            catch (StaleElementReferenceException) { /* The dialog was rerendered. */ }
        }
        return null;
    }

    private static bool TryOpenPostLikes(ChromeDriver driver,
        IReadOnlyCollection<IWebElement> existingDialogs, CancellationToken cancellationToken,
        out string error)
    {
        error = string.Empty;
        var attempts = new List<string>();
        // Try a real mouse action first. A DOM click is not equivalent to a
        // browser pointer click and may silently do nothing in Instagram.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindPostLikesDialog(driver, existingDialogs) is not null)
                return true;

            var trigger = WaitFor(driver, () => FindPostLikesTrigger(driver), cancellationToken);
            if (trigger is null)
            {
                error = "Не найден безопасный счётчик лайков публикации. " +
                    string.Join("; ", attempts);
                return false;
            }

            try
            {
                var diagnostic = DescribeSelectedLikesTrigger(driver, trigger);
                ((IJavaScriptExecutor)driver).ExecuteScript(
                    "arguments[0].scrollIntoView({block:'center',inline:'nearest'});", trigger);
                if (attempt == 0)
                    new Actions(driver).MoveToElement(trigger).Click().Perform();
                else if (attempt == 1)
                    trigger.Click();
                else
                    ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", trigger);
                attempts.Add($"Попытка {attempt + 1}: {diagnostic}, нажатие выполнено");
            }
            catch (WebDriverException exception)
            {
                attempts.Add($"Попытка {attempt + 1}: {exception.GetType().Name}");
            }

            for (var check = 0; check < 10; check++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FindPostLikesDialog(driver, existingDialogs) is not null)
                    return true;
                WaitWithCancellation(250, cancellationToken);
            }
        }

        error = "Список лайков не открылся после автоматических нажатий. " +
            string.Join("; ", attempts) + ".";
        return false;
    }

    private static string DescribeSelectedLikesTrigger(ChromeDriver driver, IWebElement trigger)
    {
        // No page HTML, URLs or account names in diagnostics.
        var result = ((IJavaScriptExecutor)driver).ExecuteScript("""
            const e=arguments[0], r=e.getBoundingClientRect();
            const hit=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);
            const label=(e.innerText||e.getAttribute('aria-label')||'').trim();
            return `tag=${e.tagName.toLowerCase()}, role=${e.getAttribute('role')||'-'}, `+
                `numeric=${/\d/.test(label)}, covered=${!!hit&&!e.contains(hit)&&!hit.contains(e)}`;
            """, trigger);
        return result?.ToString() ?? "сведения об элементе недоступны";
    }

    private static IWebElement? FindPostLikesTrigger(ChromeDriver driver)
    {
        // Instagram can render the likes count as a bare number (for example,
        // <div role="button" tabindex="0"><span>31</span></div>).
        // Locate the clickable wrapper by proximity to the heart icon rather
        // than depending on the changing number or generated CSS classes.
        var numericButton = FindNumericLikesButtonNearHeart(driver);
        if (numericButton is not null)
            return numericButton;

        // A liked_by link is unambiguous and must take priority over nearby
        // numerical buttons (comments and views may also have counts).
        foreach (var link in driver.FindElements(By.CssSelector(
            "article a[href*='liked_by'], main a[href*='liked_by']")))
        {
            try
            {
                if (link.Displayed && link.Enabled && !ContainsHeartIcon(link))
                    return link;
            }
            catch (StaleElementReferenceException) { /*ignore*/}
        }

        foreach (var counter in driver.FindElements(By.XPath(
            InstagramTaskVerificationRules.LIKES_COUNTER_AFTER_HEART_XPATH)))
        {
            try
            {
                if (counter.Displayed && counter.Enabled &&
                    counter.Text.Any(char.IsDigit) && !ContainsHeartIcon(counter))
                    return counter;
            }
            catch (StaleElementReferenceException) {/*ignore*/ }
        }

        // Unlabelled counters must not be guessed by clicking arbitrary buttons.
        foreach (var element in driver.FindElements(By.CssSelector(
            "article button, article [role='button'], main button, main [role='button']")))
        {
            try
            {
                if (!element.Displayed || !element.Enabled || ContainsHeartIcon(element)) continue;
                var label = element.Text + " " + element.GetAttribute("aria-label");
                if (InstagramTaskVerificationRules.IsLikesCounterLabel(label)) return element;
            }
            catch (StaleElementReferenceException) { /*ignore*/}
        }
        return null;
    }

    private static IWebElement? FindNumericLikesButtonNearHeart(ChromeDriver driver)
    {
        // Run geometry matching in one DOM snapshot, avoiding stale Selenium
        // references. The button must be close to the heart and must not contain
        // the heart itself; the comments count is lower on the page.
        const string script = """
            const heart = [...document.querySelectorAll('svg[aria-label]')].find(svg =>
                ['Like', 'Unlike', 'Нравится', 'Больше не нравится'].includes(svg.getAttribute('aria-label')) &&
                svg.getBoundingClientRect().width > 0);
            if (!heart) return null;
            const h = heart.getBoundingClientRect();
            const hx = h.left + h.width / 2;
            const hy = h.top + h.height / 2;
            const scope = heart.closest('article') || heart.closest('main') || document.querySelector('main');
            if (!scope) return null;
            const candidates = [...scope.querySelectorAll('div[role="button"][tabindex], button, a[href]')]
                .filter(el => {
                    if (el.contains(heart) || el.querySelector('svg[aria-label="Like"], svg[aria-label="Unlike"], svg[aria-label="Нравится"], svg[aria-label="Больше не нравится"]')) return false;
                    const value = (el.innerText || '').trim().replace(/\s/g, '');
                    if (!/^\d[\d.,]*$/.test(value)) return false;
                    const r = el.getBoundingClientRect();
                    if (r.width === 0 || r.height === 0) return false;
                    const dx = Math.abs((r.left + r.width / 2) - hx);
                    const dy = (r.top + r.height / 2) - hy;
                    return dx < 120 && dy > -12 && dy < 65;
                })
                .sort((a,b) => {
                    const ar=a.getBoundingClientRect(), br=b.getBoundingClientRect();
                    const ad=Math.abs((ar.left+ar.width/2)-hx)+Math.abs((ar.top+ar.height/2)-hy);
                    const bd=Math.abs((br.left+br.width/2)-hx)+Math.abs((br.top+br.height/2)-hy);
                    return ad-bd;
                });
            return candidates[0] || null;
            """;
        return ((IJavaScriptExecutor)driver).ExecuteScript(script) as IWebElement;
    }

    private static bool ContainsHeartIcon(IWebElement element)
        => element.FindElements(By.CssSelector(
            "svg[aria-label='Like'], svg[aria-label='Unlike'], " +
            "svg[aria-label='Нравится'], svg[aria-label='Больше не нравится']")).Count > 0;

    private static bool IsInstagramProfileHref(string? href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
            uri.Host is not ("www.instagram.com" or "instagram.com")) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length == 1 && segments[0].Length > 0 &&
            segments[0] is not ("p" or "reel" or "explore" or "accounts");
    }

    private static bool HasExactUserLink(IWebElement dialog, string user)
        => dialog.FindElements(By.CssSelector("a[href]"))
            .Any(link => InstagramTaskVerificationRules.IsExactProfileLink(link.GetAttribute("href"), user));

    private static bool IsMoreCommentsButton(IWebElement element)
    {
        var label = (element.GetAttribute("aria-label") ?? "") + " " + element.Text;
        return label.Contains("more comments", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("view more", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("показать ещё", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("загрузить", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ScrollInstagramDialog(ChromeDriver driver,
        IReadOnlyCollection<IWebElement> existingDialogs)
    {
        // React can replace the dialog or individual rows while the list loads.
        // Reacquire every Selenium reference after a stale-element failure.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var dialog = FindPostLikesDialog(driver, existingDialogs);
                if (dialog is null) return false;
                var script = (IJavaScriptExecutor)driver;

                // One atomic DOM operation to choose and scroll the actual list.
                var moved = (bool)(script.ExecuteScript("""
                    const root = arguments[0];
                    const nodes = [root, ...root.querySelectorAll('*')];
                    const candidates = nodes.filter(el =>
                        el.clientHeight > 0 && el.scrollHeight > el.clientHeight + 10 &&
                        el.getClientRects().length > 0);
                    candidates.sort((a, b) => {
                        const count = el => el.querySelectorAll('a[href]').length;
                        return count(b) - count(a) ||
                            (b.scrollHeight - b.clientHeight) - (a.scrollHeight - a.clientHeight);
                    });
                    for (const el of candidates) {
                        const before = el.scrollTop;
                        const limit = el.scrollHeight - el.clientHeight;
                        if (limit - before <= 2) continue;
                        el.scrollTop = Math.min(limit,
                            before + Math.max(120, Math.floor(el.clientHeight * 0.65)));
                        if (el.scrollTop > before + 2) return true;
                    }
                    return false;
                    """, dialog) ?? false);
                if (moved) return true;

                // Find a new wheel origin immediately before the gesture.
                // Avoid retaining a row element across an asynchronous DOM update.
                var target = script.ExecuteScript("""
                    const root = arguments[0];
                    const links = [...root.querySelectorAll('a[href]')].filter(a => {
                        const r = a.getBoundingClientRect();
                        const d = root.getBoundingClientRect();
                        return r.width > 0 && r.height > 0 && r.bottom > d.top &&
                            r.top < d.bottom && r.right > d.left && r.left < d.right;
                    });
                    return links[0] || root;
                    """, dialog) as IWebElement;
                if (target is null) return false;

                var before = (string?)script.ExecuteScript("""
                    const root = arguments[0];
                    return JSON.stringify([root, ...root.querySelectorAll('*')]
                        .filter(el => el.scrollHeight > el.clientHeight + 4)
                        .map(el => [el.scrollTop, el.scrollHeight, el.clientHeight]));
                    """, dialog);

                var origin = new WheelInputDevice.ScrollOrigin
                {
                    Element = target
                };
                new Actions(driver)
                    .ScrollFromOrigin(origin, 0, 350)
                    .Perform();

                WaitWithCancellation(350, CancellationToken.None);
                // Do not use the old dialog after wheel input: Instagram may
                // replace the entire subtree when it loads more rows.
                var freshDialog = FindPostLikesDialog(driver, existingDialogs);
                if (freshDialog is null) return false;
                var after = (string?)script.ExecuteScript("""
                    const root = arguments[0];
                    return JSON.stringify([root, ...root.querySelectorAll('*')]
                        .filter(el => el.scrollHeight > el.clientHeight + 4)
                        .map(el => [el.scrollTop, el.scrollHeight, el.clientHeight]));
                    """, freshDialog);
                return !string.Equals(before, after, StringComparison.Ordinal);
            }
            catch (StaleElementReferenceException)
            {
                // A transient DOM replacement is not a failed Instagram check.
                WaitWithCancellation(150, CancellationToken.None);
            }
            catch (WebDriverException)
            {
                return false;
            }
        }
        return false;
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
        Action<InstagramBatchProgress> onResult, CancellationToken cancellationToken = default,
        Action<InstagramBatchBrowserStage>? onStage = null)
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
                cancellationToken.ThrowIfCancellationRequested();
                onStage?.Invoke(InstagramBatchBrowserStage.STARTING_CHROME);
                var driver = GetDriver();
                onStage?.Invoke(InstagramBatchBrowserStage.CHECKING_SESSION);
                driver.Navigate().GoToUrl("https://www.instagram.com/");
                cancellationToken.ThrowIfCancellationRequested();
                if (driver.Manage().Cookies.GetCookieNamed("sessionid") is null)
                    throw new InvalidOperationException("Сессия Instagram отсутствует. Выполните вход в Chrome.");

                var done = 0;
                foreach (var (kind, members) in new[]
                         { ("followers", followersMembers), ("following", followingMembers) })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (members.Length == 0) continue;
                    onStage?.Invoke(kind == "followers"
                        ? InstagramBatchBrowserStage.CHECKING_FOLLOWERS
                        : InstagramBatchBrowserStage.CHECKING_FOLLOWING);
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

    /// <summary>
    /// Closes only Chrome started and owned by this service. The persistent Chrome
    /// profile (including the Instagram login) remains on disk for the next run.
    /// Do not pass the cancelled batch token: cleanup must always be attempted.
    /// </summary>
    public async Task CloseBrowserAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            await Task.Run(CloseDriver);
        }
        finally { _gate.Release(); }
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

public enum InstagramBatchBrowserStage
{
    STARTING_CHROME,
    CHECKING_SESSION,
    CHECKING_FOLLOWERS,
    CHECKING_FOLLOWING
}

public sealed record InstagramBatchProgress(string UserName, string Kind,
    InstagramSearchResult Result, int Completed, int Total);

public sealed record InstagramBatchRunResult(int Completed, int Total, string? StopReason);
