using System.Text.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Reads a single author's active Stories, one slide at a time. The only clicks
/// are opening the viewer, pausing playback and navigating between slides;
/// the heart is never clicked. Every decision is tied to the paused, current
/// slide and the author's visible profile link.
/// </summary>
internal static class InstagramStoryViewer
{
    private const int MaxSlides = 150;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // The DOM structure and labels were captured from Instagram's desktop viewer
    // on 25 November 2026. Generated CSS classes are deliberately not used.
    // The active progress segment is the only segment containing an inline
    // transform:translateX(...) child. The progress track's grandparent also
    // contains the author's profile link and the playback control.
    private const string ViewerScript = """
        const owner = String(arguments[0] || '').trim().toLowerCase();
        const command = String(arguments[1] || 'read');
        const expectedIndex = Number(arguments[2]);
        const empty = (error, action = 'missing') => JSON.stringify({
            present: false, owner: '', index: -1, count: 0,
            paused: false, playing: false, heart: 'missing', action, error
        });
        const visible = element => {
            if (!element || !element.isConnected) return false;
            const r = element.getBoundingClientRect();
            const s = getComputedStyle(element);
            return r.width > 0 && r.height > 0 && r.right > 0 && r.bottom > 0 &&
                r.left < innerWidth && r.top < innerHeight &&
                s.visibility !== 'hidden' && s.display !== 'none';
        };
        if (command === 'start') {
            const prompts = [...document.querySelectorAll('[role="button"],button')]
                .filter(b => visible(b) && b.textContent.trim() === 'Посмотреть историю');
            if (prompts.length !== 1) return empty('start_not_found');
            prompts[0].click();
            return empty('', 'clicked');
        }
        const path = location.pathname.split('/').filter(Boolean);
        if (path.length < 2 || path[0].toLowerCase() !== 'stories' ||
            path[1].toLowerCase() !== owner) return empty('wrong_story_owner');

        const candidates = [];
        for (const progress of document.querySelectorAll('div[style*="translateX"]')) {
            if (!/^translateX\(-?[\d.]+%\)$/.test(progress.style.transform.trim())) continue;
            const segment = progress.parentElement;
            const track = segment?.parentElement;
            const root = track?.parentElement?.parentElement;
            if (!root || !track || !segment) continue;
            const segments = [...track.children];
            const index = segments.indexOf(segment);
            if (index < 0 || segments.length < 1 || segments.length > 150 ||
                segments.some(el => el.tagName !== 'DIV')) continue;
            const tr = track.getBoundingClientRect();
            if (!visible(track) || tr.width < 100 || tr.height > 35) continue;
            const ownerLink = [...root.querySelectorAll('a[href]')].some(a => {
                try {
                    return visible(a) && new URL(a.href, location.href).pathname
                        .replace(/\/+$/, '').toLowerCase() === '/' + owner;
                } catch { return false; }
            });
            if (!ownerLink) continue;
            candidates.push({track, root, index, count: segments.length, rect: tr,
                distance: Math.abs((tr.left + tr.right) / 2 - innerWidth / 2)});
        }
        if (!candidates.length) return empty('progress_not_found');
        candidates.sort((a,b) => a.distance - b.distance);
        if (candidates.length > 1 && Math.abs(candidates[0].distance -
            candidates[1].distance) < 25) return empty('progress_ambiguous');
        const selected = candidates[0];
        const rect = selected.rect;
        const controls = [...selected.root.querySelectorAll('svg[aria-label]')].filter(visible);
        const label = svg => (svg.getAttribute('aria-label') || '').trim().toLowerCase();
        // Both states are identified by the *action* label of the visible button.
        // IG can use either a short label or the full action description.
        const play = controls.filter(svg => /^(воспроизвести|продолжить|play|resume)( воспроизведение)?$/.test(label(svg)));
        const pause = controls.filter(svg => /^(пауза|pause|приостановить|приостановить воспроизведение|pause story)$/.test(label(svg)));
        if (play.length > 1 || pause.length > 1 || (play.length && pause.length))
            return empty('playback_ambiguous');

        let action = 'none';
        if (command === 'pause') {
            if (play.length === 1) action = 'already_paused';
            else if (pause.length === 1) {
                const button = pause[0].closest('[role="button"],button');
                if (!button || !visible(button)) return empty('pause_button_not_found');
                button.click();
                return JSON.stringify({present:true, owner, index:selected.index,
                    count:selected.count, paused:false, playing:true,
                    heart:'missing', action:'clicked', error:''});
            } else return empty('pause_button_not_found');
        }
        if (command === 'prev' || command === 'next') {
            if (selected.index !== expectedIndex) return empty('slide_changed');
            if ((command === 'prev' && selected.index === 0) ||
                (command === 'next' && selected.index === selected.count - 1))
                return empty('slide_boundary');
            // The arrow can also lead to a neighbouring account. Never click
            // "Next" when the selected slide is already the author's last one.
            const arrowLabel = command === 'prev' ? ['назад','back'] : ['далее','next'];
            const edge = command === 'prev' ? rect.left : rect.right;
            const arrows = [...document.querySelectorAll('svg[aria-label]')]
                .filter(svg => arrowLabel.includes(label(svg)) && visible(svg))
                .map(svg => {
                    const button = svg.closest('[role="button"],button');
                    if (!button || !visible(button)) return null;
                    const r = button.getBoundingClientRect();
                    if (r.width > 130 || r.height > 130) return null;
                    const x = (r.left + r.right) / 2;
                    const y = (r.top + r.bottom) / 2;
                    if (y < rect.bottom + 25 || y > innerHeight - 20) return null;
                    if (command === 'prev' && (x < rect.left - 180 || x > rect.left + 45))
                        return null;
                    if (command === 'next' && (x < rect.right - 45 || x > rect.right + 180))
                        return null;
                    return {button, distance: Math.abs(x - edge)};
                }).filter(Boolean).sort((a,b) => a.distance - b.distance);
            if (!arrows.length) return empty('arrow_not_found');
            if (arrows.length > 1 && Math.abs(arrows[0].distance -
                arrows[1].distance) < 25) return empty('arrow_ambiguous');
            arrows[0].button.click();
            return JSON.stringify({present:true, owner, index:selected.index,
                count:selected.count, paused:play.length===1, playing:pause.length===1,
                heart:'missing', action:'clicked', error:''});
        }
        const liked = new Set(['не нравится','больше не нравится','unlike','liked','unlike story']);
        const unliked = new Set(['нравится','like','like story']);
        const hearts = new Map();
        for (const svg of document.querySelectorAll('svg[aria-label]')) {
            const text = label(svg);
            if (!liked.has(text) && !unliked.has(text) || !visible(svg)) continue;
            const button = svg.closest('[role="button"],button');
            if (!button || !visible(button)) continue;
            const r = button.getBoundingClientRect();
            const icon = svg.getBoundingClientRect();
            const x = (icon.left + icon.right) / 2;
            const y = (icon.top + icon.bottom) / 2;
            if (r.width > 130 || r.height > 130 ||
                x < rect.left - 30 || x > rect.right + 30 ||
                y < rect.bottom + 50 || y >= innerHeight) continue;
            const heart = liked.has(text) ? 'liked' : 'not_liked';
            if (hearts.has(button) && hearts.get(button) !== heart) hearts.set(button, 'ambiguous');
            else hearts.set(button, heart);
        }
        const heart = hearts.size > 1 ? 'ambiguous' :
            hearts.size === 1 ? [...hearts.values()][0] : 'missing';
        return JSON.stringify({present:true, owner, index:selected.index,
            count:selected.count, paused:play.length===1, playing:pause.length===1,
            heart, action, error:''});
        """;

    private sealed record StoryState(
        bool Present, string Owner, int Index, int Count, bool Paused,
        bool Playing, string Heart, string Action, string Error);

    private sealed record PauseResult(StoryState? State, string FailureReason);

    public static InstagramTaskCheckResult CheckAllSlides(
        ChromeDriver driver, string storyUrl, string owner,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        driver.Navigate().GoToUrl(storyUrl);
        // The viewer can render progress bars and playback controls BEHIND the
        // "Посмотреть историю" consent prompt. A valid progress track therefore
        // does not prove that the story is actually open. Always handle the
        // prompt first, using a real WebDriver click (rather than a synthetic
        // JavaScript click), and only then acquire the current slide.
        StoryState? state = null;
        var startClicks = 0;
        var stableViewerReads = 0;
        for (var attempt = 0; attempt < 65; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompts = FindVisibleStartPrompts(driver);
            if (prompts.Count > 1)
                return Inconclusive("Instagram показал несколько кнопок «Посмотреть историю». Не удалось выбрать нужную.");
            if (prompts.Count == 1)
            {
                stableViewerReads = 0;
                if (startClicks >= 5)
                    return Inconclusive("Кнопка «Посмотреть историю» остаётся на экране после нажатия. Просмотр не открылся.");
                try
                {
                    prompts[0].Click();
                    startClicks++;
                    progress?.Report($"@{owner}: открываем просмотр Stories…");
                }
                catch (StaleElementReferenceException)
                {
                    // React replaced the prompt while Selenium tried to click.
                    // Reacquire it instead of treating this as a missing Story.
                }
                Wait(300, cancellationToken);
                continue;
            }

            state = Control(driver, owner, "read");
            if (IsValid(state, owner))
            {
                // Wait for a few consecutive reads: the prompt can appear
                // slightly after navigation while Instagram hydrates the page.
                stableViewerReads++;
                if (stableViewerReads >= 4 && FindVisibleStartPrompts(driver).Count == 0)
                    break;
            }
            else
            {
                stableViewerReads = 0;
                if (state.Error == "wrong_story_owner" && attempt > 5)
                    return Inconclusive("Instagram открыл страницу другого аккаунта вместо Stories.");
            }
            Wait(200, cancellationToken);
        }
        if (!IsValid(state, owner) || stableViewerReads < 4)
            return Inconclusive("Не удалось открыть просмотр Stories или определить индикаторы слайдов.");
        // Do not lock onto the slide number until the viewer is actually paused.
        // Instagram may auto-advance while the prompt is closing or while a
        // pause click is being processed; that is normal at initial opening.
        var initialPause = PauseCurrentStory(driver, owner, cancellationToken);
        state = initialPause.State;
        if (state is null)
            return Inconclusive($"Не удалось поставить Stories на паузу перед проверкой. {initialPause.FailureReason}");

        // A direct URL can open at any slide. Rewind only inside this author's
        // progress track, verifying exactly one backwards step each time.
        while (state.Index > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"@{owner}: возвращаемся к первому слайду ({state.Index + 1}/{state.Count})…");
            var oldIndex = state.Index;
            var move = Control(driver, owner, "prev", oldIndex);
            if (move.Action != "clicked")
                return Inconclusive($"Не удалось перейти назад со слайда {oldIndex + 1}: {Describe(move.Error)}.");
            state = AwaitSlide(driver, owner, oldIndex - 1, state.Count, cancellationToken);
            if (state is null)
                return Inconclusive($"После перехода назад не удалось подтвердить слайд {oldIndex}.");
        }

        var missingLikes = new List<int>();
        var total = state.Count;
        for (var index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"@{owner}: проверяем лайк Stories {index + 1} из {total}…");
            state = PauseAndRead(driver, owner, index, total, cancellationToken);
            if (state is null)
                return Inconclusive($"Слайд {index + 1}/{total}: просмотр не на паузе или изменился порядок Stories.");
            if (state.Heart is "missing" or "ambiguous")
            {
                // Loading can briefly hide the heart; do not interpret that
                // as an absent like (and never click the reaction button).
                for (var retry = 0; retry < 7; retry++)
                {
                    Wait(180, cancellationToken);
                    state = PauseAndRead(driver, owner, index, total, cancellationToken);
                    if (state is null) break;
                    if (state.Heart is "liked" or "not_liked") break;
                }
            }
            if (state is null || state.Heart is not ("liked" or "not_liked"))
                return Inconclusive($"Слайд {index + 1}/{total}: состояние сердечка не удалось определить.");

            // Two observations on the same paused slide: avoid false success
            // while React is changing the displayed story.
            Wait(100, cancellationToken);
            var confirmation = PauseAndRead(driver, owner, index, total, cancellationToken);
            if (confirmation is null || confirmation.Heart != state.Heart)
                return Inconclusive($"Слайд {index + 1}/{total}: состояние лайка изменилось во время чтения.");
            if (state.Heart == "not_liked") missingLikes.Add(index + 1);

            if (index == total - 1) break; // Never follow the arrow into another author's Stories.
            var next = Control(driver, owner, "next", index);
            if (next.Action != "clicked")
                return Inconclusive($"Не удалось перейти со слайда {index + 1} на следующий: {Describe(next.Error)}.");
            state = AwaitSlide(driver, owner, index + 1, total, cancellationToken);
            if (state is null)
                return Inconclusive($"Не удалось подтвердить переход на слайд {index + 2}/{total}.");
        }
        return missingLikes.Count == 0
            ? InstagramTaskCheckResult.Confirmed($"Лайки подтверждены для всех {total} Stories @{owner}.")
            : Inconclusive($"@{owner}: лайк отсутствует на слайдах {string.Join(", ", missingLikes)} из {total}.");
    }

    private static List<IWebElement> FindVisibleStartPrompts(ChromeDriver driver)
    {
        // Based on the exact role and text in the supplied Instagram markup.
        // Do not use Instagram-generated CSS classes as selectors.
        var buttons = driver.FindElements(By.XPath(
            "//*[@role='button' and normalize-space(.)='Посмотреть историю'] | " +
            "//button[normalize-space(.)='Посмотреть историю']"));
        var visible = new List<IWebElement>();
        foreach (var button in buttons)
        {
            try
            {
                if (button.Displayed && button.Enabled)
                    visible.Add(button);
            }
            catch (StaleElementReferenceException)
            {
                // The prompt disappeared or was recreated during rendering.
            }
        }
        return visible;
    }

    /// <summary>
    /// Acquires the *currently visible* slide in a confirmed paused state.
    /// Unlike normal per-slide verification, the initial pause must tolerate
    /// an automatic transition while the browser is opening the viewer.
    /// A slide number and the story count are committed only AFTER pause.
    /// </summary>
    private static PauseResult PauseCurrentStory(ChromeDriver driver, string owner,
        CancellationToken cancellationToken)
    {
        var reason = "Состояние воспроизведения не определено.";
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = Control(driver, owner, "read");
            if (!IsValid(state, owner))
            {
                if (state.Error == "wrong_story_owner")
                    return new PauseResult(null, "Instagram переключился на истории другого аккаунта.");
                reason = $"Не удалось прочитать текущий слайд ({state.Error}).";
                Wait(160, cancellationToken);
                continue;
            }

            if (state.Paused && !state.Playing)
            {
                // Confirm the state with a second read; otherwise a DOM
                // transition could be mistaken for a stable paused slide.
                Wait(100, cancellationToken);
                var confirmed = Control(driver, owner, "read");
                if (IsValid(confirmed, owner) && confirmed.Paused && !confirmed.Playing &&
                    confirmed.Index == state.Index && confirmed.Count == state.Count)
                    return new PauseResult(confirmed, string.Empty);
                reason = "Слайд изменился во время подтверждения паузы.";
            }
            else if (state.Playing && !state.Paused)
            {
                var pause = Control(driver, owner, "pause");
                if (pause.Action is "clicked" or "already_paused")
                {
                    // React can update the icon asynchronously. Re-read it
                    // rather than assuming that a click paused playback.
                    Wait(220, cancellationToken);
                    continue;
                }
                reason = pause.Error == "pause_button_not_found"
                    ? "Кнопка паузы исчезла или не распознана на активном слайде."
                    : $"Не удалось нажать кнопку паузы ({pause.Error}).";
            }
            else
            {
                reason = "Не найдена однозначная кнопка «Пауза» или «Воспроизвести» в активной Stories.";
            }
            Wait(160, cancellationToken);
        }
        return new PauseResult(null, reason);
    }

    private static StoryState? PauseAndRead(ChromeDriver driver, string owner,
        int expectedIndex, int count, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = Control(driver, owner, "read");
            if (IsValid(state, owner) && state.Index == expectedIndex && state.Count == count)
            {
                if (state.Paused && !state.Playing) return state;
                if (state.Playing && !state.Paused)
                    Control(driver, owner, "pause");
            }
            else if (IsValid(state, owner) && (state.Index != expectedIndex || state.Count != count))
                return null;
            else if (state.Error == "wrong_story_owner") return null;
            Wait(140, cancellationToken);
        }
        return null;
    }

    private static StoryState? AwaitSlide(ChromeDriver driver, string owner, int expectedIndex,
        int count, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = Control(driver, owner, "read");
            if (IsValid(state, owner) && state.Count == count)
            {
                if (state.Index == expectedIndex)
                    return PauseAndRead(driver, owner, expectedIndex, count, cancellationToken);
                // A skipped or automatically advanced slide invalidates the run.
                if (Math.Abs(state.Index - expectedIndex) > 1) return null;
            }
            else if (state.Error == "wrong_story_owner" ||
                     (IsValid(state, owner) && state.Count != count)) return null;
            Wait(140, cancellationToken);
        }
        return null;
    }

    private static StoryState Control(ChromeDriver driver, string owner,
        string command, int expectedIndex = -1)
    {
        var json = ((IJavaScriptExecutor)driver)
            .ExecuteScript(ViewerScript, owner, command, expectedIndex)?.ToString();
        return JsonSerializer.Deserialize<StoryState>(json ?? "null", JsonOptions)
            ?? new StoryState(false, "", -1, 0, false, false, "missing", "missing", "no_response");
    }

    private static bool IsValid(StoryState? state, string owner) =>
        state is { Present: true } &&
        string.Equals(state.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
        state.Count is >= 1 and <= MaxSlides && state.Index >= 0 && state.Index < state.Count;

    private static string Describe(string error) => error switch
    {
        "arrow_not_found" => "кнопка навигации не найдена",
        "arrow_ambiguous" => "несколько кнопок навигации",
        "slide_changed" => "текущий слайд изменился до нажатия",
        "slide_boundary" => "достигнута граница историй аккаунта",
        "wrong_story_owner" => "Instagram переключился на другой аккаунт",
        _ => "разметка Stories изменилась"
    };

    private static InstagramTaskCheckResult Inconclusive(string reason) =>
        InstagramTaskCheckResult.Inconclusive(reason);

    private static void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
            cancellationToken.ThrowIfCancellationRequested();
    }
}
