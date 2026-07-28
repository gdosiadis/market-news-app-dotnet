using Microsoft.Playwright;
using MarketNewsApp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Globalization;
using System.Xml.Linq;

namespace MarketNewsApp.Services;

public class Scraper
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    private readonly IReadOnlyList<SiteConfig> _sites;

    public Scraper(IReadOnlyList<SiteConfig> sites)
    {
        _sites = sites;
    }

    // Test-only entry point used by `--debug-dom` to exercise the real screenshot capture
    // pipeline (lazy-load triggering, media filtering, blank detection, retargeting) against
    // an already-navigated page, without needing a full SiteConfig/ScrapeSiteAsync run.
    public static Task<List<string>> DebugCaptureScreenshotsAsync(IPage page) =>
        CaptureScreenshotsAsync(page, new SiteConfig { Name = "debug", Url = page.Url, Selectors = [], WaitFor = "" });

    public async Task<Dictionary<string, ScrapedSite>> ScrapeAllAsync()
    {
        var results = new Dictionary<string, ScrapedSite>();
        using var semaphore = new SemaphoreSlim(5);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            // Some sites detect the default headless-Chromium automation fingerprint and
            // serve a stripped-down page. These flags plus the init script in
            // ScrapeSiteAsync reduce that fingerprint.
            Args = ["--disable-blink-features=AutomationControlled"],
        });

        var tasks = _sites.Select(async site =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await ScrapeSiteAsync(browser, site);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var completedResults = await Task.WhenAll(tasks);
        foreach (var (name, data) in completedResults)
        {
            results[name] = data;
            Console.WriteLine($"  {(data.IsOk ? "OK" : "WARN")}  {name}  ({data.Url})");
            if (!string.IsNullOrWhiteSpace(data.Diagnostics))
                Console.WriteLine($"      ↳ {data.Diagnostics}");
        }

        return results;
    }

    // Signatures of known bot-detection / block pages. Checked against page body text so
    // failures can be attributed to a specific cause instead of a generic "no content".
    // Kept narrow/specific — only used for the low-content (<200 chars) CAUSE check, where
    // false positives are less likely because there's so little text to match against.
    private static readonly (string Marker, string Reason)[] BlockSignatures =
    [
        ("edgesuite.net", "Akamai CDN block/challenge page (network-level, not solvable via in-page interaction)"),
        ("Reference #", "Akamai/edge error reference page"),
        ("Access Denied", "Explicit access-denied response"),
        ("Pardon Our Interruption", "Bot-detection interstitial (Incapsula/Imperva-style)"),
        ("Request unsuccessful", "CDN/WAF rejected the request"),
        ("Please verify you are a human", "CAPTCHA / human-verification challenge"),
        ("captcha", "CAPTCHA challenge present"),
    ];

    // Signatures of consent/legal/login gates that hide real content behind an
    // acknowledgement or authentication step our overlay-dismissal couldn't clear.
    private static readonly (string Marker, string Reason)[] GateSignatures =
    [
        ("INSTITUTIONAL USE ONLY", "Institutional-investor disclaimer gate (needs explicit accept click)"),
        ("accept button", "Legal disclaimer gate awaiting an accept click"),
        ("Register for access", "Content gated behind account registration"),
        ("Please select your role", "Audience/role-selector gate blocking the real article"),
    ];

    private async Task<(string Name, ScrapedSite Data)> ScrapeSiteAsync(IBrowser browser, SiteConfig site)
    {
        var context = await browser.NewContextAsync(new()
        {
            UserAgent = UserAgent,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            Locale = "en-US",
        });

        // Mask the most common headless-automation tells before any page script runs.
        await context.AddInitScriptAsync("""
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
            Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
            window.chrome = window.chrome || { runtime: {} };
            """);

        var diag = new List<string>();
        var page = await context.NewPageAsync();
        try
        {
            var response = await page.GotoAsync(site.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = site.Timeout });
            var status = response?.Status ?? 0;
            diag.Add($"HTTP {status}");
            if (status is >= 400 or 0)
                diag.Add($"⚠ non-success HTTP status ({status}) — likely blocked before page rendered");

            try
            {
                await page.WaitForSelectorAsync(site.WaitFor, new() { Timeout = 8000 });
            }
            catch (TimeoutException)
            {
                diag.Add($"wait-for selector '{site.WaitFor}' timed out (page may not have hydrated)");
            }

            var recentArticleTexts = new List<string>();

            // Some sites configure a landing/list page as their Url because the real articles
            // live on separate, dynamically-named pages. Read every linked article published
            // inside the report window, rather than treating the first card as the whole source.
            if (!string.IsNullOrWhiteSpace(site.FollowFirstLinkSelector))
            {
                try
                {
                    recentArticleTexts = await ReadRecentLinkedArticlesAsync(page, site, diag);
                }
                catch (Exception ex)
                {
                    diag.Add($"article-list step failed ({ex.Message}) — staying on landing page");
                }
            }

            // Many sites gate real content behind a cookie/legal/consent modal (e.g. BlackRock's
            // "Terms and Conditions" gate) — dismiss it so extraction reaches the actual article.
            var dismissed = await DismissOverlaysAsync(page);
            diag.Add(dismissed is null ? "no consent overlay detected" : $"dismissed overlay via '{dismissed}' button");

            // Some sites truncate the real content behind a "Read more"-style toggle
            // (e.g. JPMorgan's weekly recap widget) — expand those before extraction so
            // we don't just capture a teaser snippet or the surrounding disclaimer text.
            if (site.ExpandButtonTexts.Length > 0)
            {
                var expanded = await ExpandCollapsedContentAsync(page, site.ExpandButtonTexts);
                diag.Add(expanded.Count == 0
                    ? "no expand-toggle buttons found/clicked"
                    : $"expanded {expanded.Count} toggle(s): {string.Join(", ", expanded)}");
            }

            // JS-heavy pages (React/Angular) finish hydrating after DOMContentLoaded; give them
            // more settle time so the real content is in the DOM before we extract it.
            await page.WaitForTimeoutAsync(3000);

            // Sites with tougher bot-detection (e.g. Akamai) get a longer, human-like
            // warm-up — simulated mouse movement and gradual scrolling — before we
            // give them extra settle time to clear the challenge and render real content.
            if (site.ExtraSettleMs > 0)
            {
                await HumanizeAsync(page);
                await page.WaitForTimeoutAsync(site.ExtraSettleMs);

                var bodySoFar = await page.InnerTextAsync("body");
                if (bodySoFar.Contains("edgesuite.net", StringComparison.OrdinalIgnoreCase))
                {
                    diag.Add("block page still present after humanized warm-up — retrying with reload");
                    await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = site.Timeout });
                    await DismissOverlaysAsync(page);
                    await HumanizeAsync(page);
                    await page.WaitForTimeoutAsync(site.ExtraSettleMs);
                }
            }

            // Strip known noise/duplicate containers (e.g. JPMorgan's SEO-only disclaimer
            // copy) from the DOM before extraction so they can't leak into any selector
            // match below, regardless of which selector happens to catch them.
            if (site.ExcludeSelectors.Length > 0)
            {
                var removedCount = await page.EvaluateAsync<int>(
                    @"sels => {
                        let n = 0;
                        for (const s of sels) {
                            document.querySelectorAll(s).forEach(e => { e.remove(); n++; });
                        }
                        return n;
                    }",
                    site.ExcludeSelectors);
                diag.Add($"removed {removedCount} noise element(s) via exclude selectors: {string.Join(", ", site.ExcludeSelectors)}");
            }

            // Capture chart/table elements as screenshots straight off the live page — these
            // are embedded in the email as-is (not AI-rendered), so what the recipient sees
            // is an exact visual copy of what the source actually published. Re-check for
            // consent/cookie overlays right before capturing: some sites render their cookie
            // banner with a delay (after the initial dismiss pass already ran), and a banner
            // still on-screen at capture time would otherwise show up baked into the screenshot.
            var lateOverlay = await DismissOverlaysAsync(page);
            if (lateOverlay is not null)
                diag.Add($"dismissed late-appearing overlay via '{lateOverlay}' button (pre-screenshot check)");

            // Beyond cookie/consent banners, sites often float unrelated promo widgets
            // (webcast CTAs, newsletter signups, video players) on top of the real
            // chart/table content. Close whatever obstruction is there — via an X/close
            // button or any dismiss control found — so it doesn't get baked into the
            // screenshot.
            var closedWidgets = await DismissObstructingWidgetsAsync(page);
            if (closedWidgets > 0)
                diag.Add($"closed {closedWidgets} obstructing widget(s) before screenshots");

            var screenshots = await CaptureScreenshotsAsync(page, site);
            diag.Add($"captured {screenshots.Count} chart/table screenshot(s)");

            var parts = new List<string>();
            foreach (var selector in site.Selectors)
            {
                try
                {
                    var elements = await page.QuerySelectorAllAsync(selector);
                    var matched = 0;
                    foreach (var el in elements.Take(50))
                    {
                        var text = await el.InnerTextAsync();
                        if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length > 20)
                        {
                            parts.Add(text.Trim());
                            matched++;
                        }
                    }
                    diag.Add($"selector '{selector}': {elements.Count} found, {matched} usable");
                }
                catch (Exception ex)
                {
                    diag.Add($"selector '{selector}' failed: {ex.Message}");
                }
            }

            string rawText;
            string cleanedText;
            if (parts.Count > 0)
            {
                rawText = string.Join("\n", parts);
                cleanedText = CleanText(rawText);
            }
            else
            {
                rawText = await page.InnerTextAsync("body");
                cleanedText = CleanText(rawText);
                diag.Add("no selectors matched usable content — fell back to full body text");
            }

            DateTimeOffset? publishedAt = null;
            if (recentArticleTexts.Count > 0)
            {
                rawText = string.Join("\n\n", recentArticleTexts);
                cleanedText = CleanText(rawText);
                diag.Add($"combined {recentArticleTexts.Count} article(s) published within the last 10 days");
            }
            else
            {
                // Single-page sites (no article-list step above already stamps its own
                // per-article date) — best-effort detect the article's publish date from
                // meta tags / JSON-LD / visible date text so every scraped source carries
                // a date the reader can trust, not just an implicit "scraped today".
                try
                {
                    publishedAt = await GetPublishedDateAsync(page);
                }
                catch (Exception ex)
                {
                    diag.Add($"publish-date detection failed: {ex.Message}");
                }

                if (!string.IsNullOrWhiteSpace(cleanedText))
                {
                    var dateLine = publishedAt is not null
                        ? $"Ημερομηνία δημοσίευσης άρθρου: {publishedAt.Value:dd/MM/yyyy}"
                        : $"Ημερομηνία δημοσίευσης άρθρου: άγνωστη (ανακτήθηκε {DateTimeOffset.UtcNow:dd/MM/yyyy})";
                    cleanedText = $"{dateLine}\n\n{cleanedText}";
                }
                diag.Add(publishedAt is not null
                    ? $"detected publish date: {publishedAt.Value:yyyy-MM-dd}"
                    : "no publish date detected — falling back to scrape date in text");
            }

            // Attribute the cause when extraction produced little/no usable text, so failures
            // are diagnosable without re-running with a debugger attached.
            var matchedBlock = BlockSignatures.FirstOrDefault(s => rawText.Contains(s.Marker, StringComparison.OrdinalIgnoreCase));
            var matchedGate = GateSignatures.FirstOrDefault(s => rawText.Contains(s.Marker, StringComparison.OrdinalIgnoreCase));

            if (cleanedText.Length < 200)
            {
                if (matchedBlock != default)
                    diag.Add($"❌ CAUSE: {matchedBlock.Reason}");
                else if (matchedGate != default)
                    diag.Add($"❌ CAUSE: {matchedGate.Reason}");
                else
                    diag.Add("❌ CAUSE: unknown — content extremely short with no recognized block/gate signature; inspect selectors or add a signature");
            }
            else
            {
                // Even when plenty of text was extracted, a block/gate signature appearing
                // near the start of the result usually means we captured the disclaimer/
                // login page itself rather than real article content (e.g. JPMorgan's
                // "institutional investor" legal gate leaking into the extracted selectors).
                var head = cleanedText.Length > 800 ? cleanedText[..800] : cleanedText;
                var headBlock = BlockSignatures.FirstOrDefault(s => head.Contains(s.Marker, StringComparison.OrdinalIgnoreCase));
                var headGate = GateSignatures.FirstOrDefault(s => head.Contains(s.Marker, StringComparison.OrdinalIgnoreCase));
                if (headBlock != default)
                    diag.Add($"⚠ SUSPECTED CAUSE: extracted text starts with a block signature — {headBlock.Reason} (content may still be the block page, not the article, despite non-trivial length)");
                else if (headGate != default)
                    diag.Add($"⚠ SUSPECTED CAUSE: extracted text starts with a gate signature — {headGate.Reason} (likely captured the disclaimer/login page instead of the real article; overlay-dismissal did not clear it)");
            }

            diag.Add($"final extracted length: {cleanedText.Length} chars");

            return (site.Name, new ScrapedSite
            {
                Url = page.Url,
                Text = string.IsNullOrWhiteSpace(cleanedText) ? $"[{site.Name}: no content extracted]" : cleanedText,
                Diagnostics = string.Join(" | ", diag),
                Screenshots = screenshots,
                PublishedDate = publishedAt
            });
        }
        catch (TimeoutException ex)
        {
            diag.Add($"❌ CAUSE: page load timed out after {site.Timeout}ms ({ex.Message})");
            return (site.Name, new ScrapedSite { Url = site.Url, Text = $"[{site.Name}: page load timed out]", Diagnostics = string.Join(" | ", diag) });
        }
        catch (Exception ex)
        {
            diag.Add($"❌ CAUSE: unhandled exception — {ex.GetType().Name}: {ex.Message}");
            return (site.Name, new ScrapedSite { Url = site.Url, Text = $"[{site.Name}: error — {ex.Message}]", Diagnostics = string.Join(" | ", diag) });
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    // Generic selectors for chart/table elements, used when a SiteConfig doesn't specify its
    // own ScreenshotSelectors override. Broad on purpose (charts are implemented very
    // differently across sites — SVG, canvas, plain <img>, or styled <table> markup) —
    // size filtering + the media/text checks in CaptureScreenshotsAsync weed out small
    // icons/logos and non-visual (pure text) matches.
    //
    // NOTE: bare "figure" is intentionally NOT in this list — <figure> is used for both
    // real data charts AND decorative photos/pull-quotes across these sites, and there's
    // no reliable selector-only way to tell them apart. CaptureScreenshotsAsync instead
    // requires a figure to contain real chart media (svg/canvas/table) or an image with a
    // caption (typical of a data chart's "Source: ..." line) before accepting it.
    private static readonly string[] DefaultScreenshotSelectors =
    [
        "table",
        "svg",
        "canvas",
        "figure",
        "[class*='chart' i]",
        "[class*='graph' i]",
        "[id*='chart' i]",
    ];

    private const int MaxScreenshotsPerSite = 6;

    // Screenshots chart/table elements directly off the live, rendered page so the email
    // shows an exact visual copy of what the source published — no AI re-rendering involved.
    // Matches are de-duplicated by bounding box (broad selectors like "svg" and "table" often
    // overlap the same element) and filtered by minimum size to skip decorative icons/logos.
    //
    // Two extra guards keep screenshots limited to ONLY graphs/tables (never plain text) and
    // never blank/empty:
    //  1. HasChartOrTableMediaAsync — rejects an element unless it IS a table/svg/canvas, or
    //     CONTAINS one, or (for a bare <figure>/chart-class container) has an <img> together
    //     with a caption — this filters out pull-quotes, photo figures, and paragraph text
    //     blocks that happened to match a broad class-name selector.
    //  2. IsBlankScreenshot — decodes the captured PNG and rejects it if it's essentially a
    //     single flat color (an unloaded chart placeholder, empty ad slot, etc.).
    // Sticky/fixed-position banners (promo bars, sticky nav headers, cookie-preference tabs
    // that never fully close, etc.) sit on top of the page's normal scroll flow, so even
    // after DismissObstructingWidgetsAsync closes actual popups, one of these can still visibly
    // overlap a chart/table when Playwright scrolls it into view for a screenshot (e.g. a
    // "Learn More" promo bar drawn over the top of a chart). Rather than trying to enumerate
    // every possible promo-bar selector per site, just hide anything CSS-fixed/sticky right
    // before capturing screenshots — nothing in that layer is ever the chart/table itself.
    private static async Task HideFixedOverlaysAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(
                @"() => {
                    document.querySelectorAll('body *').forEach(el => {
                        const pos = getComputedStyle(el).position;
                        if (pos === 'fixed' || pos === 'sticky') {
                            el.style.setProperty('display', 'none', 'important');
                        }
                    });
                }");
        }
        catch
        {
            // Best-effort — if this fails (e.g. navigation in progress) just proceed without it.
        }
    }

    private static async Task<List<string>> CaptureScreenshotsAsync(IPage page, SiteConfig site)
    {
        var screenshots = new List<string>();
        var selectors = site.ScreenshotSelectors.Length > 0 ? site.ScreenshotSelectors : DefaultScreenshotSelectors;
        var seenBoxes = new HashSet<string>();

        await HideFixedOverlaysAsync(page);
        await TriggerLazyLoadedImagesAsync(page);

        foreach (var selector in selectors)
        {
            if (screenshots.Count >= MaxScreenshotsPerSite) break;
            IReadOnlyList<IElementHandle> elements;
            try
            {
                elements = await page.QuerySelectorAllAsync(selector);
            }
            catch
            {
                continue;
            }

            foreach (var el in elements)
            {
                if (screenshots.Count >= MaxScreenshotsPerSite) break;
                try
                {
                    var box = await el.BoundingBoxAsync();
                    if (box is null || box.Width < 150 || box.Height < 80) continue;

                    var key = $"{Math.Round(box.X)}_{Math.Round(box.Y)}_{Math.Round(box.Width)}_{Math.Round(box.Height)}";
                    if (!seenBoxes.Add(key)) continue;

                    if (!await HasChartOrTableMediaAsync(el)) continue;

                    var target = await ResolveScreenshotTargetAsync(el);
                    if (target is null) continue;

                    await target.ScrollIntoViewIfNeededAsync();
                    if (!await WaitForImagesToLoadAsync(target)) continue;

                    var bytes = await target.ScreenshotAsync(new() { Type = ScreenshotType.Png });
                    if (IsBlankScreenshot(bytes)) continue;
                    screenshots.Add(Convert.ToBase64String(bytes));
                }
                catch
                {
                    // Element may have detached, be off-screen, or fail to render (e.g. zero-
                    // opacity) — skip it rather than aborting the whole capture pass.
                }
            }
        }

        return screenshots;
    }

    // Some sites (e.g. BlackRock) only load a chart's actual <img> once it scrolls into
    // view (IntersectionObserver-based lazy loading), and bundle the chart together with
    // several unrelated paragraphs inside one oversized CMS content block. Scrolling
    // through the whole page once before capturing anything gives every lazy image a
    // chance to start loading, so by the time we get to each element it's more likely
    // to already be rendered instead of still blank.
    private static async Task TriggerLazyLoadedImagesAsync(IPage page)
    {
        try
        {
            var height = await page.EvaluateAsync<int>("() => document.body.scrollHeight");
            const int step = 800;
            for (var y = 0; y < height; y += step)
            {
                await page.EvaluateAsync("y => window.scrollTo(0, y)", y);
                await page.WaitForTimeoutAsync(150);
            }
            await page.EvaluateAsync("() => window.scrollTo(0, 0)");
            await page.WaitForTimeoutAsync(200);
        }
        catch
        {
            // Best-effort — if this fails just proceed without it.
        }
    }

    // Picks the element to actually screenshot, which is not always the element that
    // matched our selector:
    //   - table/svg/canvas ARE the chart — screenshot them directly.
    //   - A container (figure/[class*=chart] div/etc.) that wraps a table/svg/canvas
    //     descendant: screenshot that descendant directly. These tags are inherently
    //     self-contained, so this is always safe and avoids the container's own padding.
    //   - A container that only wraps a raster <img>: some CMSes (e.g. BlackRock) bundle
    //     the chart image together with several unrelated paragraphs inside one oversized
    //     content block, so screenshotting the whole container can capture way more text
    //     than just the chart. Only trust the container's own bounding box when it's a
    //     reasonably tight fit around the image (chart + short caption, as on sites like
    //     Edward Jones/Citi); otherwise screenshot the <img> element itself instead.
    private static async Task<IElementHandle?> ResolveScreenshotTargetAsync(IElementHandle el)
    {
        var tagName = await el.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
        if (tagName is "table" or "svg" or "canvas") return el;

        // Decorative icons (e.g. a tiny expand/collapse chevron <svg> next to a
        // "Chart description" toggle link) can also match "svg, canvas, table" — only
        // trust this descendant as the real chart if it's a plausible chart/table size,
        // otherwise fall through to the container/img handling below.
        var innerVisual = await el.QuerySelectorAsync("svg, canvas, table");
        if (innerVisual is not null)
        {
            var innerBox = await innerVisual.BoundingBoxAsync();
            if (innerBox is not null && innerBox.Width >= 150 && innerBox.Height >= 80)
            {
                return innerVisual;
            }
        }

        var containerTextLen = await el.EvaluateAsync<int>("el => (el.innerText || '').length");
        if (containerTextLen <= 400) return el;

        return await el.QuerySelectorAsync("img");
    }

    // Waits briefly for any <img> within the target (or the target itself, if it IS an
    // <img>) to finish loading (non-zero naturalWidth), so we don't email a still-blank
    // lazy-loaded placeholder. Returns false (skip this screenshot) if nothing loads in
    // time; true if the target has no <img> at all (e.g. it's an svg/canvas/table, which
    // render synchronously and don't need this wait).
    private static async Task<bool> WaitForImagesToLoadAsync(IElementHandle target)
    {
        try
        {
            var isImgItself = await target.EvaluateAsync<bool>("el => el.tagName.toLowerCase() === 'img'");
            var hasImgDescendant = isImgItself || await target.EvaluateAsync<bool>("el => !!el.querySelector('img')");
            if (!hasImgDescendant) return true;

            return await target.EvaluateAsync<bool>(
                @"async el => {
                    const imgs = el.tagName.toLowerCase() === 'img' ? [el] : Array.from(el.querySelectorAll('img'));
                    if (imgs.length === 0) return true;
                    const waitOne = (img) => new Promise(resolve => {
                        if (img.complete && img.naturalWidth > 0) { resolve(true); return; }
                        const timer = setTimeout(() => resolve(img.naturalWidth > 0), 4000);
                        img.addEventListener('load', () => { clearTimeout(timer); resolve(true); }, { once: true });
                        img.addEventListener('error', () => { clearTimeout(timer); resolve(false); }, { once: true });
                    });
                    const results = await Promise.all(imgs.map(waitOne));
                    return results.some(ok => ok);
                }");
        }
        catch
        {
            return true; // Don't block the capture on an unexpected evaluation failure.
        }
    }

    // Decides whether a matched element is actually a graph/table (and not plain text,
    // a pull-quote, or a decorative photo) before we bother screenshotting it.
    //   - <table>/<svg>/<canvas> themselves always qualify — they ARE the chart/table.
    //   - Anything else (a "figure" tag or a "[class*=chart/graph]" container div) only
    //     qualifies if it contains a real table/svg/canvas descendant, OR contains an <img>
    //     AND the caption/text explicitly cites a data source (e.g. "Source: ...",
    //     "Chart: ...", "Data: ..."). A bare <figcaption> is NOT enough on its own — news
    //     photos (e.g. Bloomberg's oil-tank photo) commonly have a figcaption too, but it's
    //     a photo credit ("Photographer: X/Bloomberg"), not a data citation. Requiring the
    //     explicit source/chart/data keyword is what actually distinguishes a data chart
    //     figure from a decorative photo figure.
    private static async Task<bool> HasChartOrTableMediaAsync(IElementHandle el)
    {
        try
        {
            var tagName = await el.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
            if (tagName is "table" or "svg" or "canvas") return true;

            return await el.EvaluateAsync<bool>(
                @"el => {
                    // Only count a nested svg/canvas/table as real chart media if it's a
                    // plausible chart size — decorative icons (e.g. a tiny expand/collapse
                    // chevron next to a 'Chart description' toggle link) are also <svg> but
                    // should not qualify.
                    const visual = el.querySelector('svg, canvas, table');
                    if (visual) {
                        const r = visual.getBoundingClientRect();
                        if (r.width >= 150 && r.height >= 80) return true;
                    }
                    const img = el.querySelector('img');
                    if (!img) return false;
                    const text = (el.innerText || '').toLowerCase();
                    return text.includes('source:') || text.includes('source ')
                        || text.includes('chart:') || text.includes('data:');
                }");
        }
        catch
        {
            return false;
        }
    }

    // Detects near-blank/empty screenshots (e.g. an unloaded chart placeholder, an empty
    // ad slot, or a chart container that rendered with no data yet) so we don't email a
    // useless flat-color image. Downsamples to a small grid and buckets pixel colors —
    // a real chart/table has many distinct shades (gridlines, bars, borders, text); a
    // blank placeholder collapses to essentially one or two colors.
    private static bool IsBlankScreenshot(byte[] pngBytes)
    {
        try
        {
            using var image = Image.Load<Rgba32>(pngBytes);
            image.Mutate(ctx => ctx.Resize(32, 32));

            var seenColorBuckets = new HashSet<int>();
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        // Bucket similar shades together (round to nearest 16 per channel) so
                        // anti-aliasing/compression noise doesn't count as real "variety".
                        var bucket = ((p.R / 16) << 16) | ((p.G / 16) << 8) | (p.B / 16);
                        seenColorBuckets.Add(bucket);
                    }
                }
            });

            return seenColorBuckets.Count <= 2;
        }
        catch
        {
            // If we can't decode/analyze it for any reason, don't block on it — err on the
            // side of keeping the screenshot rather than silently dropping real content.
            return false;
        }
    }

    // Generic close-button patterns for floating/sticky promo widgets, video overlays, and
    // newsletter/webcast popups that can sit on top of chart/table content (distinct from
    // the cookie/consent overlays handled by DismissOverlaysAsync). We don't care *how* the
    // obstruction disappears — clicking any close/X/dismiss control is fine as long as it's
    // gone before the screenshot is taken.
    private static readonly string[] CloseButtonSelectors =
    [
        "[aria-label='Close' i]",
        "[aria-label='Dismiss' i]",
        "[aria-label='Close dialog' i]",
        "[aria-label='Close modal' i]",
        "[aria-label='Close this' i]",
        "[title='Close' i]",
        "button[class*='close' i]",
        "[role='button'][class*='close' i]",
        "[class*='close' i][class*='button' i]",
        "button:text-is('×')",
        "button:text-is('✕')",
        "button:text-is('X')",
    ];

    private static readonly string[] CloseButtonTexts =
    [
        "No Thanks", "No thanks", "Not Now", "Not now", "Maybe Later", "Maybe later",
        "Skip", "Dismiss", "Close",
    ];

    // Builds a Locator matching any of the given tag/attribute selectors that contains the
    // target button text. Short, generic single words (e.g. "OK", "Close", "Agree") use an
    // EXACT whole-element-text match (":text-is") — otherwise a plain substring match would
    // also fire on unrelated text elsewhere that merely CONTAINS those letters (e.g. "OK" is
    // a literal substring of "Book"/"Look"/"Broker"), which previously caused a wrong click
    // and navigated away from the real article. Longer, multi-word phrases (e.g. "Continue
    // Without Accepting") are matched with a substring search (":has-text") instead, since
    // real buttons for those often include extra icon/screen-reader text around the visible
    // label that would fail an exact match, while the specific phrase itself is far too long
    // to accidentally collide with unrelated text.
    private static ILocator BuildButtonTextLocator(IPage page, string text, params string[] tagSelectors)
    {
        var useExactMatch = !text.Contains(' ') && text.Length <= 8;
        var pseudo = useExactMatch ? "text-is" : "has-text";
        var combined = string.Join(", ", tagSelectors.Select(tag => $"{tag}:{pseudo}('{text}')"));
        return page.Locator(combined).First;
    }

    // Best-effort removal of non-consent obstructions (promo widgets, sticky CTAs, video
    // overlays) that can float on top of chart/table content. Runs a handful of passes
    // since closing one widget can reveal another stacked behind it; stops as soon as a
    // pass finds nothing left to close.
    private static async Task<int> DismissObstructingWidgetsAsync(IPage page)
    {
        var closedCount = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            var closedThisPass = false;

            foreach (var selector in CloseButtonSelectors)
            {
                try
                {
                    var button = page.Locator(selector).First;
                    if (await button.IsVisibleAsync(new() { Timeout = 500 }))
                    {
                        await ClickWithJsFallbackAsync(button);
                        await page.WaitForTimeoutAsync(300);
                        closedCount++;
                        closedThisPass = true;
                        break;
                    }
                }
                catch { }
            }

            if (!closedThisPass)
            {
                foreach (var text in CloseButtonTexts)
                {
                    try
                    {
                        var button = BuildButtonTextLocator(page, text, "button", "[role='button']");
                        if (await button.IsVisibleAsync(new() { Timeout = 500 }))
                        {
                            await ClickWithJsFallbackAsync(button);
                            await page.WaitForTimeoutAsync(300);
                            closedCount++;
                            closedThisPass = true;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (!closedThisPass) break;
        }
        return closedCount;
    }

    // Simulates plausible human interaction (mouse movement + gradual scrolling) to help
    // pass behavioral bot-detection checks (e.g. Akamai) that headless automation typically
    // fails by staying perfectly still.
    private static async Task HumanizeAsync(IPage page)
    {
        var rng = Random.Shared;
        try
        {
            for (var i = 0; i < 4; i++)
            {
                await page.Mouse.MoveAsync(rng.Next(50, 1200), rng.Next(50, 700), new() { Steps = rng.Next(5, 15) });
                await page.WaitForTimeoutAsync(rng.Next(150, 400));
            }

            for (var scrolled = 0; scrolled < 3; scrolled++)
            {
                await page.Mouse.WheelAsync(0, rng.Next(300, 700));
                await page.WaitForTimeoutAsync(rng.Next(400, 900));
            }
        }
        catch { }
    }

    private static async Task<List<string>> ReadRecentLinkedArticlesAsync(IPage page, SiteConfig site, List<string> diagnostics)
    {
        var listedArticles = await page.EvaluateAsync<ListedArticle[]>("""
            selector => Array.from(document.querySelectorAll(selector)).map(link => {
                const card = link.closest('.chip, article, li, [class*="card" i], [class*="article" i]');
                return {
                    href: link.getAttribute('href') ?? '',
                    dateText: [
                        card?.getAttribute('data-date'),
                        card?.querySelector('time')?.getAttribute('datetime'),
                        card?.querySelector('time')?.textContent,
                        card?.textContent
                    ].filter(Boolean).join(' ')
                };
            })
            """, site.FollowFirstLinkSelector!);
        var articleUrls = new List<(string Url, DateTimeOffset? PublishedAt)>();
        foreach (var article in listedArticles)
        {
            if (string.IsNullOrWhiteSpace(article.Href)) continue;

            if (Uri.TryCreate(new Uri(page.Url), article.Href, out var resolved) &&
                !articleUrls.Any(item => item.Url.Equals(resolved.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                var listedDate = TryParsePublishedDate(article.DateText);
                articleUrls.Add((resolved.ToString(), listedDate));
            }
        }

        diagnostics.Add($"article-list selector '{site.FollowFirstLinkSelector}': {articleUrls.Count} unique link(s) found");
        var cutoff = DateTimeOffset.UtcNow.Date.AddDays(-10);
        var articleTexts = new List<string>();
        var sitemapDates = await LoadSitemapDatesAsync(articleUrls.Select(article => article.Url));

        foreach (var article in articleUrls.Take(20))
        {
            try
            {
            await page.GotoAsync(article.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = site.Timeout });
                await page.WaitForTimeoutAsync(1000);

                var publishedAt = article.PublishedAt
                    ?? (sitemapDates.TryGetValue(article.Url, out var sitemapDate) ? (DateTimeOffset?)sitemapDate : null)
                    ?? await GetPublishedDateAsync(page);
                if (publishedAt is null)
                {
                    diagnostics.Add($"skipped article with no detectable publication date: {page.Url}");
                    continue;
                }

                if (publishedAt.Value.Date < cutoff)
                {
                    diagnostics.Add($"skipped article published {publishedAt.Value:yyyy-MM-dd}: {page.Url}");
                    continue;
                }

                await DismissOverlaysAsync(page);
                var text = await ExtractPageTextAsync(page, site.Selectors);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    articleTexts.Add($"Άρθρο ({publishedAt.Value:dd/MM/yyyy}) — {page.Url}\n{text}");
                    diagnostics.Add($"read article published {publishedAt.Value:yyyy-MM-dd}: {page.Url}");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"skipped article after load/extraction failure ({ex.Message}): {article.Url}");
            }
        }

        return articleTexts;
    }

    private sealed class ListedArticle
    {
        public string Href { get; set; } = "";
        public string DateText { get; set; } = "";
    }

    // Reliable, structured date sources — always trusted first regardless of value, since
    // they're explicitly semantic (the page itself is declaring "this is when it was
    // published").
    private static async Task<string[]> GetStructuredDateCandidatesAsync(IPage page) =>
        await page.EvaluateAsync<string[]>("""
            () => {
                const values = [];
                document.querySelectorAll('meta[property="article:published_time"], meta[itemprop="datePublished"], time[datetime]')
                    .forEach(element => values.push(element.getAttribute('content') ?? element.getAttribute('datetime') ?? element.textContent ?? ''));

                document.querySelectorAll('script[type="application/ld+json"]').forEach(script => {
                    try {
                        const collectDates = value => {
                            if (Array.isArray(value)) return value.forEach(collectDates);
                            if (value && typeof value === 'object') {
                                if (typeof value.datePublished === 'string') values.push(value.datePublished);
                                if (value['@graph']) collectDates(value['@graph']);
                            }
                        };
                        collectDates(JSON.parse(script.textContent ?? ''));
                    } catch { }
                });
                return values.filter(Boolean);
            }
            """);

    // Weaker, generic date sources (page-level meta like "date"/"publish-date", which some
    // sites populate with an unrelated last-modified/copyright date instead of the article's
    // actual publish date, plus loosely-classed on-page text). Only trusted when the parsed
    // value actually looks like a plausible recent article date (see GetPublishedDateAsync).
    private static async Task<string[]> GetLooseDateCandidatesAsync(IPage page) =>
        await page.EvaluateAsync<string[]>("""
            () => {
                const values = [];
                document.querySelectorAll('meta[name="date"], meta[name="publish-date"]')
                    .forEach(element => values.push(element.getAttribute('content') ?? ''));
                document.querySelectorAll('[class*="date" i], [class*="publish" i], [data-testid*="date" i]')
                    .forEach(element => values.push(element.textContent ?? ''));
                return values.filter(Boolean);
            }
            """);

    private static async Task<Dictionary<string, DateTimeOffset>> LoadSitemapDatesAsync(IEnumerable<string> articleUrls)
    {
        var articleUrlSet = articleUrls.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (articleUrlSet.Count == 0) return [];

        var sitemapUrl = new Uri(new Uri(articleUrlSet.First()), "/sitemap.xml");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var sitemap = XDocument.Parse(await client.GetStringAsync(sitemapUrl));
            XNamespace sitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
            return sitemap.Descendants(sitemapNamespace + "url")
                .Select(entry => new
                {
                    Url = entry.Element(sitemapNamespace + "loc")?.Value,
                    Date = TryParsePublishedDate(entry.Element(sitemapNamespace + "lastmod")?.Value ?? ""),
                })
                .Where(entry => entry.Url is not null && entry.Date is not null && articleUrlSet.Contains(entry.Url))
                .ToDictionary(entry => entry.Url!, entry => entry.Date!.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    // Structured dates (JSON-LD/meta[article:published_time]/<time>) are normally trusted
    // outright as an explicit publish-date declaration, but some sites' structured markup
    // has turned out to be stale/unrelated to the actual displayed article (e.g. a leftover
    // template date years in the past) rather than genuinely wrong-but-recent, so every
    // candidate — structured or generic class-based text — is validated against a plausible
    // recency window for a weekly/periodic market commentary before being accepted. If
    // nothing in either tier falls in that window we report "unknown" rather than a
    // confidently wrong stale date.
    private static async Task<DateTimeOffset?> GetPublishedDateAsync(IPage page)
    {
        var recencyWindowStart = DateTimeOffset.UtcNow.AddDays(-45);
        var recencyWindowEnd = DateTimeOffset.UtcNow.AddDays(2);
        bool InWindow(DateTimeOffset date) => date >= recencyWindowStart && date <= recencyWindowEnd;

        var structured = await GetStructuredDateCandidatesAsync(page);
        var structuredMatch = structured
            .Select(TryParsePublishedDate)
            .FirstOrDefault(date => date is not null && InWindow(date.Value));
        if (structuredMatch is not null) return structuredMatch;

        var loose = await GetLooseDateCandidatesAsync(page);
        return loose
            .Select(TryParsePublishedDate)
            .FirstOrDefault(date => date is not null && InWindow(date.Value));
    }

    private static DateTimeOffset? TryParsePublishedDate(string candidate) =>
        DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var publishedAt)
            ? publishedAt
            : null;

    private static async Task<string> ExtractPageTextAsync(IPage page, IReadOnlyList<string> selectors)
    {
        var parts = new List<string>();
        foreach (var selector in selectors)
        {
            try
            {
                var elements = await page.QuerySelectorAllAsync(selector);
                foreach (var element in elements.Take(50))
                {
                    var text = await element.InnerTextAsync();
                    if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length > 20)
                        parts.Add(text.Trim());
                }
            }
            catch
            {
                // A malformed optional selector should not discard other recent articles.
            }
        }

        return CleanText(parts.Count > 0 ? string.Join("\n", parts) : await page.InnerTextAsync("body"));
    }

    private static string CleanText(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 30)
            .Take(250);
        return string.Join("\n", lines);
    }

    // Best-effort dismissal of cookie/legal/terms consent overlays that block the real
    // content underneath (e.g. BlackRock's "accept Terms and Conditions" gate).
    //
    // Preference order: for pure cookie-consent banners we prefer clicking a reject/decline
    // option first — so screenshots don't end up with leftover cookie banners/consent chrome
    // visible in them — and only fall back to an accept-style button when no reject option
    // exists (many legal/institutional-investor disclaimer gates, e.g. JPMorgan, only offer
    // an "accept" control, since declining would just re-block the article content).
    private static readonly string[] RejectButtonTexts =
    [
        "Reject All", "Reject all", "Reject All Cookies", "Decline All", "Decline all",
        "Decline", "Reject", "I Decline", "Do Not Accept", "Refuse All", "Refuse all",
        "Only Necessary", "Necessary Only", "Necessary only", "Continue Without Accepting",
    ];

    private static readonly string[] ConsentButtonTexts =
    [
        "Accept All", "Accept all", "Accept All Cookies", "I Accept", "I Agree",
        "I agree", "Agree", "Allow All", "Allow all", "Accept", "Got it", "Continue", "OK",
        // Legal/institutional-investor disclaimer gates (e.g. JPMorgan) often render their
        // accept control as a link or ARIA button rather than a plain <button>, with
        // wording like these instead of a generic "Accept".
        "Accept and Continue", "Enter Site", "I Understand", "I understand", "Confirm",
    ];

    private static async Task<string?> DismissOverlaysAsync(IPage page)
    {
        // Reject/decline controls are only matched against real <button>/role="button"
        // elements — NOT <a> links. Cookie-consent "reject" wording can coincidentally
        // match unrelated navigation links elsewhere on the page (e.g. a footer/legal
        // link containing the word "Decline"), which would navigate away from the
        // article entirely. Genuine cookie-banner reject controls are essentially
        // always real buttons, so this restriction is safe and avoids that failure mode.
        foreach (var text in RejectButtonTexts)
        {
            try
            {
                var button = BuildButtonTextLocator(page, text, "button", "[role='button']");
                if (await button.IsVisibleAsync(new() { Timeout = 800 }))
                {
                    await ClickWithJsFallbackAsync(button);
                    await page.WaitForTimeoutAsync(500);
                    return text;
                }
            }
            catch { }
        }

        foreach (var text in ConsentButtonTexts)
        {
            try
            {
                // Some sites render the accept control as a link or ARIA role="button"
                // <div>/<span> rather than a real <button> element (e.g. JPMorgan's
                // disclaimer gate), so check all three shapes, not just <button>.
                var button = BuildButtonTextLocator(page, text, "button", "a", "[role='button']");
                if (await button.IsVisibleAsync(new() { Timeout = 800 }))
                {
                    await ClickWithJsFallbackAsync(button);
                    await page.WaitForTimeoutAsync(500);
                    return text;
                }
            }
            catch { }
        }
        return null;
    }

    // Clicks an element, falling back to a JS-level el.click() dispatch if Playwright's
    // real-mouse click times out. This happens on sites (e.g. JPMorgan's disclaimer gate)
    // where an unrelated, effectively invisible overlay sits on top at the same screen
    // coordinates and swallows real pointer events even though the target itself is
    // visible, enabled and on-screen — a JS click bypasses screen coordinates entirely
    // and invokes the handler directly, so it isn't intercepted.
    private static async Task ClickWithJsFallbackAsync(ILocator button)
    {
        try
        {
            await button.ClickAsync(new() { Timeout = 2000 });
        }
        catch (TimeoutException)
        {
            await button.EvaluateAsync("el => el.click()");
        }
    }

    // Clicks known "expand"/"read more"-style toggles that reveal truncated content.
    // Unlike consent overlays (one-shot, dismiss-and-done), a page can have multiple
    // independent expanders (e.g. several collapsed sections), so every match for every
    // configured button text is clicked rather than stopping at the first one found.
    private static async Task<List<string>> ExpandCollapsedContentAsync(IPage page, string[] buttonTexts)
    {
        var clicked = new List<string>();
        foreach (var text in buttonTexts)
        {
            try
            {
                var buttons = page.Locator($"button:has-text('{text}'), a:has-text('{text}')");
                var count = await buttons.CountAsync();
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var button = buttons.Nth(i);
                        if (await button.IsVisibleAsync(new() { Timeout = 500 }))
                        {
                            await ClickWithJsFallbackAsync(button);
                            await page.WaitForTimeoutAsync(400);
                            clicked.Add(text);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return clicked;
    }
}
