using Microsoft.Playwright;
using MarketNewsApp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MarketNewsApp.Services;

public class Scraper
{
    // Listing pages can contain many eligible articles within the report window.
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromMinutes(5);

    // Several publishers flag the simultaneous five-context burst from one IP as automation,
    // while the exact same source succeeds in the single-source workflow. Keep production
    // scraping serial so every site receives the same request pattern as that workflow.
    private const int MaxConcurrentSources = 1;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    // Used only when a Greek listing source has no site-specific selector. Keeping this
    // narrow avoids opening navigation links on direct weekly-report/article pages.
    private const string GreekListingArticleLinkSelector =
        "article h2 a, article h3 a, [class*='article' i] h2 a, [class*='article' i] h3 a, [class*='card' i] h2 a";

    private readonly IReadOnlyList<SiteConfig> _sites;

    private readonly Func<string, ScrapedSite, Task>? _onSourceCompleted;

    public Scraper(IReadOnlyList<SiteConfig> sites, Func<string, ScrapedSite, Task>? onSourceCompleted = null)
    {
        _sites = sites;
        _onSourceCompleted = onSourceCompleted;
    }

    // Test-only entry point used by `--debug-dom` to exercise the real screenshot capture
    // pipeline (lazy-load triggering, media filtering, blank detection, retargeting) against
    // an already-navigated page, without needing a full SiteConfig/ScrapeSiteAsync run.
    public static Task<List<string>> DebugCaptureScreenshotsAsync(IPage page) =>
        CaptureScreenshotsAsync(page, new SiteConfig { Name = "debug", Url = page.Url, Selectors = [], WaitFor = "" });

    public async Task<Dictionary<string, ScrapedSite>> ScrapeAllAsync()
    {
        var results = new Dictionary<string, ScrapedSite>();
        using var semaphore = new SemaphoreSlim(MaxConcurrentSources);

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
                Console.WriteLine($"  …  Scraping {site.Name}");
                var scrapeTask = ScrapeSiteAsync(browser, site);
                var completedTask = await Task.WhenAny(scrapeTask, Task.Delay(PerSourceTimeout));
                if (completedTask == scrapeTask)
                {
                    var completed = await scrapeTask;
                    if (completed.Data.IsOk && _onSourceCompleted is not null)
                        await _onSourceCompleted(completed.Name, completed.Data);
                    return completed;
                }

                _ = scrapeTask.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return (site.Name, new ScrapedSite
                {
                    Url = site.Url,
                    SourceRegion = site.SourceRegion,
                    Text = $"[{site.Name}: scrape timed out]",
                    Diagnostics = $"❌ CAUSE: scrape exceeded the {PerSourceTimeout.TotalMinutes:F0}-minute per-source limit",
                });
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        while (tasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(tasks);
            tasks.Remove(completedTask);
            var (name, data) = await completedTask;
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
        var timeout = EffectiveTimeout(site);
        var context = await browser.NewContextAsync(new()
        {
            UserAgent = UserAgent,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            Locale = "en-US",
        });
        context.SetDefaultTimeout(Math.Min(timeout, 15000));
        context.SetDefaultNavigationTimeout(timeout);

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
            var response = await NavigateWithRetryAsync(page, site, diag);
            var status = response?.Status ?? 0;
            diag.Add($"HTTP {status}");
            if (status is >= 400 or 0)
                diag.Add($"⚠ non-success HTTP status ({status}) — likely blocked before page rendered");

            try
            {
                await page.WaitForSelectorAsync(site.WaitFor, new() { Timeout = ContentWaitTimeout(site) });
            }
            catch (TimeoutException)
            {
                diag.Add($"wait-for selector '{site.WaitFor}' timed out (page may not have hydrated)");
            }

            var recentArticles = new List<LinkedArticleContent>();

            // Some sites configure a landing/list page as their Url because the real articles
            // live on separate, dynamically-named pages. Read every linked article published
            // inside the report window, rather than treating the first card as the whole source.
            var articleLinkSelector = ArticleLinkSelector(site);
            if (articleLinkSelector is not null)
            {
                try
                {
                    var listingOverlay = await DismissOverlaysAsync(page);
                    if (listingOverlay is not null)
                        diag.Add($"dismissed listing overlay via '{listingOverlay}' button");
                    if (string.IsNullOrWhiteSpace(site.FollowFirstLinkSelector))
                        diag.Add("no site-specific article-link selector configured; using Greek listing auto-discovery");
                    recentArticles = await ReadRecentLinkedArticlesAsync(page, site, articleLinkSelector, diag);
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

            if (string.Equals(site.Name, "JPMorgan Weekly Market Recap", StringComparison.OrdinalIgnoreCase))
            {
                var jpmorganGate = await DismissJpmorganInstitutionalGateAsync(page);
                if (jpmorganGate is not null)
                    diag.Add(jpmorganGate);
            }

            // Some sites truncate the real content behind a "Read more"-style toggle
            // (e.g. JPMorgan's weekly recap widget) — expand those before extraction so
            // we don't just capture a teaser snippet or the surrounding disclaimer text.
            if (site.ExpandButtonTexts.Length > 0)
            {
                var expanded = new List<string>();
                if (string.Equals(site.Name, "JPMorgan Weekly Market Recap", StringComparison.OrdinalIgnoreCase))
                {
                    var jpmorganReadMore = page.Locator("#wmr-readmore-button");
                    try
                    {
                        await jpmorganReadMore.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
                        await ClickWithJsFallbackAsync(jpmorganReadMore);
                        await page.WaitForTimeoutAsync(500);
                        expanded.Add("Read more (#wmr-readmore-button)");
                    }
                    catch (TimeoutException)
                    {
                        diag.Add("JPMorgan Read more button did not render before extraction");
                    }
                }

                expanded.AddRange(await ExpandCollapsedContentAsync(page, site.ExpandButtonTexts));
                diag.Add(expanded.Count == 0
                    ? "no expand-toggle buttons found/clicked"
                    : $"expanded {expanded.Count} toggle(s): {string.Join(", ", expanded)}");
            }

            // JS-heavy pages (React/Angular) finish hydrating after DOMContentLoaded; give them
            // more settle time so the real content is in the DOM before we extract it.
            await page.WaitForTimeoutAsync(3000);

            // Many newsletter, video, and cookie widgets appear only after the initial
            // hydration delay. Clear them before text extraction as well as before
            // screenshots, otherwise selector/body extraction can pick up their CTA or
            // disclaimer text instead of the article underneath.
            var lateExtractionOverlay = await DismissOverlaysAsync(page);
            if (lateExtractionOverlay is not null)
                diag.Add($"dismissed late-appearing overlay via '{lateExtractionOverlay}' button (pre-extraction check)");
            var closedExtractionWidgets = await DismissObstructingWidgetsAsync(page);
            if (closedExtractionWidgets > 0)
                diag.Add($"closed {closedExtractionWidgets} obstructing widget(s) before extraction");

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
                    await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout });
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

            var screenshots = recentArticles.Count > 0
                ? []
                : await CaptureScreenshotsAsync(page, site);
            foreach (var screenshot in recentArticles.SelectMany(article => article.Screenshots))
            {
                if (screenshots.Count >= MaxScreenshotsPerSite) break;
                screenshots.Add(screenshot);
            }
            var screenshotCountBeforeDeduplication = screenshots.Count;
            screenshots = screenshots.Distinct(StringComparer.Ordinal).ToList();
            if (screenshots.Count < screenshotCountBeforeDeduplication)
                diag.Add($"removed {screenshotCountBeforeDeduplication - screenshots.Count} duplicate screenshot(s)");
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
            if (recentArticles.Count > 0)
            {
                rawText = string.Join("\n\n", recentArticles.Select(article => article.Text));
                cleanedText = CleanText(rawText);
                diag.Add($"combined {recentArticles.Count} article(s) published within the last 10 days");
            }
            else
            {
                // JPMorgan's weekly recap content is rendered from this page-local model
                // endpoint. The page shell can load successfully while its widget remains a
                // skeleton in headless Chromium, leaving no Read more button to click.
                if (cleanedText.Length < 200 && string.Equals(site.Name, "JPMorgan Weekly Market Recap", StringComparison.OrdinalIgnoreCase))
                {
                    var recap = await TryGetJpmorganWeeklyRecapAsync(page, diag);
                    if (recap is not null)
                    {
                        rawText = recap.Value.Text;
                        cleanedText = CleanText(rawText);
                        publishedAt = recap.Value.PublishedDate;
                    }
                }

                // Morning View serves an empty React shell to non-hydrated pages, while its
                // own public feed contains the current article bodies and author labels.
                // Select the stable MARKET VIEW label instead of relying on its changing slug.
                if (IsMorningView(site))
                {
                    var marketView = await TryGetMorningViewArticleAsync(diag);
                    if (marketView is not null)
                    {
                        rawText = marketView.Value.Text;
                        cleanedText = CleanText(rawText);
                        publishedAt = marketView.Value.PublishedDate;
                    }
                }

                // Single-page sites (no article-list step above already stamps its own
                // per-article date) — best-effort detect the article's publish date from
                // meta tags / JSON-LD / visible date text so every scraped source carries
                // a date the reader can trust, not just an implicit "scraped today".
                try
                {
                    publishedAt ??= await GetPublishedDateAsync(page);
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
                Url = site.Url,
                SourceRegion = site.SourceRegion,
                Text = string.IsNullOrWhiteSpace(cleanedText) ? $"[{site.Name}: no content extracted]" : cleanedText,
                Diagnostics = string.Join(" | ", diag),
                Screenshots = screenshots,
                PublishedDate = publishedAt,
                PublishedDates = recentArticles.Select(article => article.PublishedAt).Distinct().OrderByDescending(date => date).ToList()
            });
        }
        catch (TimeoutException ex)
        {
            diag.Add($"❌ CAUSE: page load timed out after {timeout}ms ({ex.Message})");
            return (site.Name, new ScrapedSite { Url = site.Url, SourceRegion = site.SourceRegion, Text = $"[{site.Name}: page load timed out]", Diagnostics = string.Join(" | ", diag) });
        }
        catch (Exception ex)
        {
            diag.Add($"❌ CAUSE: unhandled exception — {ex.GetType().Name}: {ex.Message}");
            return (site.Name, new ScrapedSite { Url = site.Url, SourceRegion = site.SourceRegion, Text = $"[{site.Name}: error — {ex.Message}]", Diagnostics = string.Join(" | ", diag) });
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static async Task<IResponse?> NavigateWithRetryAsync(IPage page, SiteConfig site, List<string> diagnostics)
    {
        var timeout = EffectiveTimeout(site);
        var waitUntil = UsesContentAwareNavigation(site) ? WaitUntilState.Commit : WaitUntilState.DOMContentLoaded;
        try
        {
            return await page.GotoAsync(site.Url, new() { WaitUntil = waitUntil, Timeout = timeout });
        }
        catch (TimeoutException)
        {
            diagnostics.Add($"initial navigation timed out after {timeout}ms; retrying at commit stage");
            await page.WaitForTimeoutAsync(1000);
            // A number of publishers keep DOMContentLoaded pending behind analytics or
            // third-party resources even though the article DOM is usable. The scraper
            // already waits for source content below, so do not fail this source on that
            // unrelated browser lifecycle event a second time.
            return await page.GotoAsync(site.Url, new() { WaitUntil = WaitUntilState.Commit, Timeout = timeout });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("net::ERR_EMPTY_RESPONSE", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add("initial navigation returned an empty network response; retrying once");
            await page.WaitForTimeoutAsync(1000);
            return await page.GotoAsync(site.Url, new() { WaitUntil = waitUntil, Timeout = timeout });
        }
    }

    private static int EffectiveTimeout(SiteConfig site) =>
        string.Equals(site.Name, "Euro2Day", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(site.Timeout, 45000)
            : UsesContentAwareNavigation(site) ? Math.Max(site.Timeout, 60000) : site.Timeout;

    private static bool UsesContentAwareNavigation(SiteConfig site) =>
        IsMorningView(site) ||
        string.Equals(site.Name, "T. Rowe Price Global Markets", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(site.Name, "JPMorgan Weekly Market Recap", StringComparison.OrdinalIgnoreCase);

    private static int ContentWaitTimeout(SiteConfig site) =>
        UsesContentAwareNavigation(site) ? 30000 : 8000;

    private static int ArticleNavigationTimeout(SiteConfig site) =>
        string.Equals(site.Name, "Euro2Day", StringComparison.OrdinalIgnoreCase)
            ? 30000
            : EffectiveTimeout(site);

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
        "svg",
        "canvas",
        "figure",
        "[class*='chart' i]",
        "[class*='graph' i]",
        "[id*='chart' i]",
        "img[data-watermark]",
        "img[class*='wp-image-']",
        "iframe[src*='datawrapper']",
        "iframe[src*='tradingview']",
        "table",
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
        var selectors = site.ScreenshotSelectors.Length > 0
            ? site.ScreenshotSelectors
            : AllowsUncaptionedChartImages(site)
                ? [.. DefaultScreenshotSelectors, "img"]
                : DefaultScreenshotSelectors;
        var seenBoxes = new HashSet<string>();

        await HideEuro2DayPrivacyPanelAsync(page, site);
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
                    if (await IsCapitalConsentOverlayAsync(el, site)) continue;

                    var box = await el.BoundingBoxAsync();
                    if (box is null || box.Width < 150 || box.Height < 80) continue;

                    var key = $"{Math.Round(box.X)}_{Math.Round(box.Y)}_{Math.Round(box.Width)}_{Math.Round(box.Height)}";
                    if (!seenBoxes.Add(key)) continue;

                    var bnpSourceImage = await TryDownloadBnpWordPressImageAsync(el, site);
                    if (bnpSourceImage is not null && !IsBlankScreenshot(bnpSourceImage))
                    {
                        screenshots.Add(Convert.ToBase64String(bnpSourceImage));
                        continue;
                    }

                    var citiSourceImage = await TryDownloadCitiWeeklyChartImageAsync(el, site);
                    if (citiSourceImage is not null && !IsBlankScreenshot(citiSourceImage))
                    {
                        screenshots.Add(Convert.ToBase64String(citiSourceImage));
                        continue;
                    }

                    if (!await HasChartOrTableMediaAsync(el, site)) continue;

                    var target = await ResolveScreenshotTargetAsync(el);
                    if (target is null) continue;

                    // A consent/promo layer can appear while lazy assets load, after the
                    // initial page cleanup. Clear it again at the exact capture boundary.
                    await DismissOverlaysAsync(page);
                    await DismissObstructingWidgetsAsync(page);
                    await HideEuro2DayPrivacyPanelAsync(page, site);
                    await HideFixedOverlaysAsync(page);
                    if (await IsCapitalConsentOverlayAsync(target, site) || await IsCaptureTargetObstructedAsync(target)) continue;

                    var sourceImage = await TryDownloadBnpWordPressImageAsync(target, site);
                    if (sourceImage is not null && !IsBlankScreenshot(sourceImage))
                    {
                        screenshots.Add(Convert.ToBase64String(sourceImage));
                        continue;
                    }

                    try
                    {
                        await target.ScrollIntoViewIfNeededAsync();
                    }
                    catch
                    {
                        await target.EvaluateAsync("el => el.scrollIntoView({ block: 'center' })");
                    }
                    await DismissOverlaysAsync(page);
                    await DismissObstructingWidgetsAsync(page);
                    await HideEuro2DayPrivacyPanelAsync(page, site);
                    await HideFixedOverlaysAsync(page);
                    if (await IsCapitalConsentOverlayAsync(target, site) || await IsCaptureTargetObstructedAsync(target)) continue;
                    if (!await WaitForImagesToLoadAsync(target)) continue;

                    byte[] bytes;
                    try
                    {
                        bytes = await target.ScreenshotAsync(new()
                        {
                            Type = ScreenshotType.Png,
                            Animations = ScreenshotAnimations.Disabled,
                        });
                    }
                    catch
                    {
                        // Citi's weekly-chart layout keeps shifting after its image has
                        // loaded, so Playwright's element screenshot never considers it
                        // stable. The target box remains valid; capture that exact live
                        // page region instead.
                        var targetBox = await target.BoundingBoxAsync();
                        if (targetBox is null) continue;
                        bytes = await page.ScreenshotAsync(new()
                        {
                            Type = ScreenshotType.Png,
                            Clip = new()
                            {
                                X = targetBox.X,
                                Y = targetBox.Y,
                                Width = targetBox.Width,
                                Height = targetBox.Height,
                            },
                        });
                    }
                    if (IsBlankScreenshot(bytes))
                    {
                        sourceImage = await TryDownloadBnpWordPressImageAsync(target, site);
                        if (sourceImage is null || IsBlankScreenshot(sourceImage)) continue;
                        bytes = sourceImage;
                    }
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

    private static async Task<bool> IsCapitalConsentOverlayAsync(IElementHandle element, SiteConfig site)
    {
        if (!string.Equals(site.Name, "Capital", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return await element.EvaluateAsync<bool>("""
                element => {
                    const consentText = /σεβ[oό]μαστε την ιδιωτικ[oό]τητ[aά] σας|privacy settings|cookie settings|cookies και [όo]χι μ[oό]νο/i;
                    for (let current = element; current && current !== document.body; current = current.parentElement) {
                        const text = current.textContent ?? '';
                        if (consentText.test(text)) return true;
                        const idAndClass = `${current.id} ${current.className}`;
                        if (/consent|privacy|cookie|sp_message/i.test(idAndClass)) return true;
                    }
                    return false;
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    // Euro2Day's privacy panel is absolutely positioned, so it survives the generic
    // fixed/sticky cleanup and can cover the left side of a table screenshot.
    private static async Task HideEuro2DayPrivacyPanelAsync(IPage page, SiteConfig site)
    {
        if (!string.Equals(site.Name, "Euro2Day", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await page.EvaluateAsync("""
                () => {
                    const panelText = /απόρρητο είναι σημαντικό|διαχειριστείτε τις προτιμήσεις σας/i;
                    for (const element of document.querySelectorAll('body *')) {
                        if (!panelText.test(element.textContent ?? '')) continue;
                        let panel = element;
                        while (panel && panel !== document.body) {
                            const style = getComputedStyle(panel);
                            const box = panel.getBoundingClientRect();
                            if ((style.position === 'absolute' || style.position === 'fixed') &&
                                box.width >= 120 && box.height >= 80) {
                                panel.style.setProperty('display', 'none', 'important');
                                break;
                            }
                            panel = panel.parentElement;
                        }
                    }
                }
                """);
        }
        catch
        {
            // Screenshot capture remains best-effort if the panel detaches during navigation.
        }
    }

    private static async Task<bool> IsCaptureTargetObstructedAsync(IElementHandle target)
    {
        try
        {
            return await target.EvaluateAsync<bool>("""
                element => {
                    const rect = element.getBoundingClientRect();
                    if (rect.width <= 0 || rect.height <= 0) return true;
                    const points = [
                        [rect.left + rect.width / 2, rect.top + rect.height / 2],
                        [rect.left + 8, rect.top + 8],
                        [rect.right - 8, rect.bottom - 8]
                    ];
                    return points.some(([x, y]) => {
                        const covering = document.elementFromPoint(x, y);
                        return covering && covering !== element && !element.contains(covering) && !covering.contains(element);
                    });
                }
                """);
        }
        catch
        {
            return true;
        }
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
    private static async Task<bool> HasChartOrTableMediaAsync(IElementHandle el, SiteConfig site)
    {
        try
        {
            var tagName = await el.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
            if (tagName is "table" or "svg" or "canvas") return true;
            if (tagName == "iframe")
                return await el.EvaluateAsync<bool>("""
                    element => /datawrapper|tradingview/i.test(element.getAttribute('src') ?? '')
                    """);
            if (tagName == "img")
                return await IsDataChartImageAsync(el, site);

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
                    const imageSignals = `${img.alt || ''} ${img.className || ''} ${img.currentSrc || img.src || ''}`.toLowerCase();
                    const hasDataCaption = text.includes('source:') || text.includes('source ')
                        || text.includes('chart:') || text.includes('data:');
                    const hasChartSignal = /chart|graph|table|datawrapper|tradingview|performance|returns|allocation|market-data/.test(imageSignals);
                    const photoSignal = /portrait|headshot|profile|author|speaker|person|people/.test(imageSignals);
                    return hasChartSignal && !photoSignal && (hasDataCaption || trustedSource);
                }""", AllowsUncaptionedChartImages(site));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsDataChartImageAsync(IElementHandle image, SiteConfig site)
    {
        try
        {
            return await image.EvaluateAsync<bool>("""
                (element, trustedSource) => {
                    const signal = `${element.alt || ''} ${element.className || ''} ${element.currentSrc || element.src || ''}`.toLowerCase();
                    const chartSignal = /chart|graph|table|datawrapper|tradingview|performance|returns|allocation|market-data/.test(signal);
                    const photoSignal = /portrait|headshot|profile|author|speaker|person|people/.test(signal);
                    if (!chartSignal || photoSignal) return false;
                    const figure = element.closest('figure, [class*="chart" i], [class*="graph" i]');
                    const context = figure?.innerText?.toLowerCase() ?? '';
                    return trustedSource || context.includes('source:') || context.includes('source ')
                        || context.includes('chart:') || context.includes('data:');
                }
                """, AllowsUncaptionedChartImages(site));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBnpViewpoint(SiteConfig site) =>
        Uri.TryCreate(site.Url, UriKind.Absolute, out var url) &&
        url.Host.EndsWith("viewpoint.bnpparibas-am.com", StringComparison.OrdinalIgnoreCase);

    // These two publishers serve charts as raster images but omit a nearby Source/Chart/Data
    // caption. Restrict the fallback to their known sources so photo figures elsewhere stay out.
    private static bool AllowsUncaptionedChartImages(SiteConfig site) =>
        string.Equals(site.Name, "BlackRock Investment Institute", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(site.Name, "Edward Jones Weekly Update", StringComparison.OrdinalIgnoreCase);

    private static bool IsCitiMarketInsights(SiteConfig site) =>
        Uri.TryCreate(site.Url, UriKind.Absolute, out var url) &&
        url.Host.EndsWith("marketinsights.citi.com", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]?> TryDownloadCitiWeeklyChartImageAsync(IElementHandle target, SiteConfig site)
    {
        if (!IsCitiMarketInsights(site)) return null;

        try
        {
            var imageUrl = await target.EvaluateAsync<string>("""
                element => element.matches('img.weekly-figure')
                    ? element.currentSrc
                    : element.querySelector('img.weekly-figure')?.currentSrc ?? ''
                """);
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
                !uri.Host.EndsWith("marketinsights.citi.com", StringComparison.OrdinalIgnoreCase))
                return null;

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return await client.GetByteArrayAsync(uri);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> TryDownloadBnpWordPressImageAsync(IElementHandle target, SiteConfig site)
    {
        if (!IsBnpViewpoint(site)) return null;

        try
        {
            var imageUrl = await target.EvaluateAsync<string>("""
                element => element.tagName.toLowerCase() === 'img'
                    ? element.currentSrc
                    : element.querySelector("img[class*='wp-image-']")?.currentSrc ?? ''
                """);
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
                !uri.Host.EndsWith("viewpoint.bnpparibas-am.com", StringComparison.OrdinalIgnoreCase))
                return null;

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return await client.GetByteArrayAsync(uri);
        }
        catch
        {
            return null;
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

    private static string? ArticleLinkSelector(SiteConfig site) =>
        IsMorningView(site) ? "a[href]"
        : !string.IsNullOrWhiteSpace(site.FollowFirstLinkSelector)
            ? site.FollowFirstLinkSelector
            : IsGreekSource(site) ? GreekListingArticleLinkSelector : null;

    private static async Task<List<LinkedArticleContent>> ReadRecentLinkedArticlesAsync(IPage page, SiteConfig site, string articleLinkSelector, List<string> diagnostics)
    {
        var listedArticles = IsMorningView(site)
            ? await page.EvaluateAsync<ListedArticle[]>("""
            () => Array.from(document.querySelectorAll('a[href]')).map((link, index) => {
                const card = link.closest('article, li, [class*="column" i], [class*="card" i]');
                return {
                    linkIndex: index,
                    href: link.getAttribute('href') ?? '',
                    dateText: card?.querySelector('time')?.getAttribute('datetime')
                        ?? card?.querySelector('time')?.textContent
                        ?? card?.textContent
                        ?? ''
                };
            }).filter(article => /MARKET\s+VIEW/i.test(article.dateText))
            """)
            : await page.EvaluateAsync<ListedArticle[]>("""
            selector => Array.from(document.querySelectorAll(selector)).map((link, index) => {
                const card = link.closest('.chip, article, li, [class*="card" i], [class*="article" i]');
                return {
                    linkIndex: index,
                    href: link.getAttribute('href') ?? '',
                    title: link.textContent ?? '',
                    dateText: [
                        card?.getAttribute('data-date'),
                        card?.querySelector('time')?.getAttribute('datetime'),
                        card?.querySelector('time')?.textContent,
                        card?.querySelector('[class*="date" i]')?.textContent,
                        card?.textContent
                    ].filter(Boolean).join(' ')
                };
            })
            """, articleLinkSelector);
        var articleUrls = new List<(string Url, string Title, DateTimeOffset? PublishedAt, int LinkIndex)>();
        foreach (var article in listedArticles)
        {
            if (string.IsNullOrWhiteSpace(article.Href)) continue;

            if (Uri.TryCreate(new Uri(page.Url), article.Href, out var resolved) &&
                IsArticleUrl(site, resolved) &&
                !articleUrls.Any(item => item.Url.Equals(resolved.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                var listedDate = TryParsePublishedDate(article.DateText);
                articleUrls.Add((resolved.ToString(), article.Title, listedDate, article.LinkIndex));
            }
        }

        diagnostics.Add($"article-list selector '{articleLinkSelector}': {articleUrls.Count} unique link(s) found");
        var cutoff = DateTimeOffset.UtcNow.Date.AddDays(-10);
        var articles = new List<LinkedArticleContent>();
        var sitemapDates = await LoadSitemapDatesAsync(articleUrls.Select(article => article.Url));

        foreach (var article in articleUrls)
        {
            IPage? articlePage = null;
            try
            {
                if (string.Equals(site.Name, "Euro2Day", StringComparison.OrdinalIgnoreCase) &&
                    !IsGreekMarketArticleTitle(article.Title))
                {
                    diagnostics.Add($"skipped non-domestic market article by title: {article.Url}");
                    continue;
                }

                var publishedAt = article.PublishedAt
                    ?? (sitemapDates.TryGetValue(article.Url, out var sitemapDate) ? (DateTimeOffset?)sitemapDate : null);
                if (publishedAt is not null && publishedAt.Value.Date < cutoff)
                {
                    diagnostics.Add($"skipped article published {publishedAt.Value:yyyy-MM-dd}: {article.Url}");
                    continue;
                }

                // Listing-card and sitemap dates let us reject stale articles without
                // loading them. Only unknown dates need a page visit for verification.
                // Use a separate tab so article navigation cannot replace the listing DOM
                // that is still needed later for the source's own text and screenshots.
                articlePage = await page.Context.NewPageAsync();
                try
                {
                    await articlePage.GotoAsync(article.Url, new()
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = ArticleNavigationTimeout(site),
                    });
                }
                catch (Exception ex) when (
                    string.Equals(site.Name, "Capital", StringComparison.OrdinalIgnoreCase) &&
                    (ex is TimeoutException || ex.Message.Contains("net::ERR_HTTP2_PROTOCOL_ERROR", StringComparison.OrdinalIgnoreCase)))
                {
                    diagnostics.Add($"article navigation failed transiently; retrying once: {article.Url}");
                    await articlePage.GotoAsync(article.Url, new()
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = ArticleNavigationTimeout(site),
                    });
                }
                await articlePage.WaitForTimeoutAsync(1000);
                diagnostics.Add($"opened article: {article.Url}");

                var articlePagePublishedAt = await GetPublishedDateAsync(articlePage);
                publishedAt = articlePagePublishedAt;
                if (publishedAt is null)
                {
                    diagnostics.Add($"skipped article with no verified publication date: {articlePage.Url}");
                    continue;
                }

                if (publishedAt.Value.Date < cutoff)
                {
                    diagnostics.Add($"skipped article published {publishedAt.Value:yyyy-MM-dd}: {page.Url}");
                    continue;
                }

                await DismissOverlaysAsync(articlePage);
                if (site.ExcludeSelectors.Length > 0)
                {
                    await articlePage.EvaluateAsync<int>(
                        @"sels => {
                            let n = 0;
                            for (const s of sels) {
                                document.querySelectorAll(s).forEach(e => { e.remove(); n++; });
                            }
                            return n;
                        }",
                        site.ExcludeSelectors);
                }
                var text = await ExtractLinkedArticleTextAsync(articlePage, site.Selectors);
                var title = await ExtractArticleTitleAsync(articlePage);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    await DismissObstructingWidgetsAsync(articlePage);
                    await HideFixedOverlaysAsync(articlePage);
                    var screenshots = await CaptureScreenshotsAsync(articlePage, site);
                    articles.Add(new LinkedArticleContent(
                        $"Άρθρο ({publishedAt.Value:dd/MM/yyyy}) — {articlePage.Url}\n{text}",
                        publishedAt.Value,
                        screenshots));
                    diagnostics.Add($"read article published {publishedAt.Value:yyyy-MM-dd}: {articlePage.Url}");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"skipped article after load/extraction failure ({ex.Message}): {article.Url}");
            }
            finally
            {
                if (articlePage is not null)
                    await articlePage.CloseAsync();
            }
        }

        return articles;
    }

    private sealed record LinkedArticleContent(string Text, DateTimeOffset PublishedAt, List<string> Screenshots);

    private static bool IsArticleUrl(SiteConfig site, Uri url)
    {
        if (string.Equals(site.Name, "Insider", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.IsMatch(url.AbsolutePath, @"^/agores/\d+/");

        if (string.Equals(site.Name, "Capital", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.IsMatch(url.AbsolutePath, @"^/agores/\d+/");

        return true;
    }

    private static bool IsGreekSource(SiteConfig site) =>
        string.Equals(site.SourceRegion, "Greek", StringComparison.OrdinalIgnoreCase);

    private static bool IsMorningView(SiteConfig site) =>
        Uri.TryCreate(site.Url, UriKind.Absolute, out var url) &&
        url.Host.EndsWith("morningview.gr", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ExtractArticleTitleAsync(IPage page)
    {
        try
        {
            return (await page.Locator("h1").First.InnerTextAsync()).Trim();
        }
        catch
        {
            return "";
        }
    }

    private static bool IsGreekMarketArticleTitle(string title)
    {
        var normalizedTitle = title
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Aggregate(new StringBuilder(), (builder, character) => builder.Append(character)).ToString()
            .ToUpperInvariant();
        return GreekMarketSignals.Any(signal => normalizedTitle.Contains(signal, StringComparison.Ordinal));
    }

    // Greek publishers mix domestic and foreign stories on the same markets feed. Check the
    // complete article because the Greek-market context is often only stated in the body.
    private static readonly string[] GreekMarketSignals =
    [
        "ΧΡΗΜΑΤΙΣΤΗΡΙΟ ΑΘΗΝΩΝ", "ΧΡΗΜΑΤΙΣΤΗΡΙΟ", "ATHEX", "Χ.Α.", "ΓΕΝΙΚΟΣ ΔΕΙΚΤΗΣ", "FTSE/ATHEX",
        "ΕΛΛΗΝΙΚΗ ΑΓΟΡ", "ΕΛΛΗΝΙΚΕΣ ΜΕΤΟΧ", "ΕΛΛΗΝΙΚΩΝ ΜΕΤΟΧ",
        "ΕΛΛΗΝΙΚΕΣ ΕΙΣΗΓΜ", "ΕΛΛΗΝΙΚΩΝ ΕΙΣΗΓΜ", "ΕΛΛΗΝΙΚΕΣ ΤΡΑΠΕΖ",
        "ΕΛΛΗΝΙΚΩΝ ΤΡΑΠΕΖ", "ΤΡΑΠΕΖΙΚΟΣ ΔΕΙΚΤ", "ΛΕΩΦΟΡΟ ΑΘΗΝΩΝ", "ΑΘΗΝΑΪΚΗ ΑΓΟΡ",
        "ΕΓΧΩΡΙΑ ΑΓΟΡ", "ΕΓΧΩΡΙΕΣ ΜΕΤΟΧ", "BLUE CHIPS", "ΕΛΛΑΔ",
    ];

    private sealed class ListedArticle
    {
        public int LinkIndex { get; set; }
        public string Href { get; set; } = "";
        public string Title { get; set; } = "";
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
                Array.from(document.querySelectorAll('body *'))
                    .filter(element => element.children.length === 0 && /^\s*(?:Published\b|Week of\s+)/i.test(element.textContent ?? ''))
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

    private static async Task<(string Text, DateTimeOffset? PublishedDate)?> TryGetMorningViewArticleAsync(List<string> diagnostics)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.GetAsync("https://www.morningview.gr/backend/api/get-articles?tagId=&page=1");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("articles", out var articles))
                return null;

            foreach (var article in articles.EnumerateArray())
            {
                var label = article.TryGetProperty("author", out var author) &&
                    author.TryGetProperty("description", out var description)
                    ? description.GetString()
                    : null;
                if (!string.Equals(label, "MARKET VIEW", StringComparison.OrdinalIgnoreCase)) continue;

                var title = article.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
                var body = article.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(body)) return null;

                var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", " "));
                var date = article.TryGetProperty("releaseDate", out var dateElement)
                    ? TryParsePublishedDate(dateElement.GetString() ?? "")
                    : null;
                diagnostics.Add("retrieved current MARKET VIEW article from Morning View's public feed");
                return ($"{title}\n{text}", date);
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Morning View public-feed fallback failed: {ex.Message}");
        }

        return null;
    }

    private static async Task<(string Text, DateTimeOffset? PublishedDate)?> TryGetJpmorganWeeklyRecapAsync(IPage page, List<string> diagnostics)
    {
        try
        {
            var modelPath = await page.GetAttributeAsync("#weekly-market-recap", "data-comp-prop-url");
            if (string.IsNullOrWhiteSpace(modelPath)) return null;

            var modelUrl = new Uri(new Uri(page.Url), modelPath).GetLeftPart(UriPartial.Path);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            using var modelDocument = JsonDocument.Parse(await client.GetStringAsync(modelUrl));
            if (!modelDocument.RootElement.TryGetProperty("wmrJson", out var recapJson)) return null;

            using var recapDocument = JsonDocument.Parse(recapJson.GetString() ?? "");
            if (!recapDocument.RootElement.TryGetProperty("WMI", out var recap)) return null;

            var sections = new List<string>();
            foreach (var name in new[] { "ThoughtOfTheWeek", "WeekInReview", "WeekAhead", "EconomicUpdate", "Equities", "Fixed Income", "Commodities", "Currencies", "Key Rates" })
            {
                if (recap.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.String)
                {
                    var value = section.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) sections.Add($"{name}: {value}");
                }
            }

            if (sections.Count == 0) return null;
            var publishedDate = recap.TryGetProperty("AsOf", out var asOf)
                ? TryParsePublishedDate(asOf.GetString() ?? "")
                : null;
            diagnostics.Add($"loaded JPMorgan weekly recap from model endpoint ({sections.Count} section(s))");
            return (string.Join("\n\n", sections), publishedDate);
        }
        catch (Exception ex)
        {
            diagnostics.Add($"JPMorgan recap model fallback failed: {ex.Message}");
            return null;
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

    // Formats seen on sites that print a plain-text date (no <time>/meta markup) using a
    // dot separator (e.g. BNP Paribas' "27.07.2026" author byline) — DateTimeOffset.TryParse
    // with invariant culture rejects these outright since '.' isn't a recognized date
    // separator for it, so they need an explicit ParseExact fallback.
    private static readonly string[] ExplicitDateFormats =
    [
        "dd.MM.yyyy", "d.M.yyyy", "dd-MM-yyyy", "d-M-yyyy",
        "MMM d, yyyy", "MMM. d, yyyy", "MMMM d, yyyy"
    ];

    private static readonly Regex TextualDatePattern = new(
        @"\b(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\.?\s+\d{1,2},\s+\d{4}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static DateTimeOffset? TryParsePublishedDate(string candidate)
    {
        if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var publishedAt))
            return publishedAt;

        var trimmed = candidate.Trim().TrimStart('|').Trim();
        if (DateTimeOffset.TryParseExact(trimmed, ExplicitDateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var exact)
            )
            return exact;

        var textualDate = TextualDatePattern.Match(trimmed);
        return textualDate.Success && DateTimeOffset.TryParseExact(textualDate.Value, ExplicitDateFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
            out exact)
            ? exact
            : null;
    }

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

    private static async Task<string> ExtractLinkedArticleTextAsync(IPage page, IReadOnlyList<string> selectors)
    {
        if (page.Url.Contains("capital.gr/agores/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = await page.EvaluateAsync<string>("""
                    () => {
                        const heading = document.querySelector('h1');
                        if (!heading) return '';
                        const paragraphs = Array.from(document.querySelectorAll('p'))
                            .filter(paragraph => Boolean(heading.compareDocumentPosition(paragraph) & Node.DOCUMENT_POSITION_FOLLOWING))
                            .map(paragraph => paragraph.textContent?.trim() ?? '')
                            .filter(text => text.length > 30)
                            .slice(0, 80);
                        return [heading.textContent?.trim() ?? '', ...paragraphs].join('\n');
                    }
                    """);
                if (CleanText(text).Length >= 300)
                    return CleanText(text);
            }
            catch { }
        }

        // A listing source's configured selectors often deliberately include broad tags such
        // as p/h2/h3. On a linked article those also match navigation and related-story cards.
        // Prefer the page's semantic content container so the AI receives the article body.
        foreach (var selector in selectors.Where(IsStructuralArticleSelector))
        {
            try
            {
                var element = page.Locator(selector).First;
                var text = await element.InnerTextAsync(new() { Timeout = 1500 });
                if (CleanText(text).Length >= 300)
                    return CleanText(text);
            }
            catch { }
        }

        foreach (var selector in new[] { "article", "main article", "[role='main'] article", "main" })
        {
            try
            {
                var element = page.Locator(selector).First;
                var text = await element.InnerTextAsync(new() { Timeout = 1500 });
                if (CleanText(text).Length >= 300)
                    return CleanText(text);
            }
            catch { }
        }

        return await ExtractPageTextAsync(page, selectors);
    }

    private static bool IsStructuralArticleSelector(string selector) =>
        !string.Equals(selector, "article", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(selector, "main", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(selector, "p", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(selector, "h1", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(selector, "h2", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(selector, "h3", StringComparison.OrdinalIgnoreCase);

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
        "Απόρριψη όλων", "Απόρριψη", "Μόνο απαραίτητα",
    ];

    private static readonly string[] ConsentButtonTexts =
    [
        "Accept All", "Accept all", "Accept All Cookies", "I Accept", "I Agree",
        "I agree", "Agree", "Allow All", "Allow all", "Accept", "Got it", "Continue", "OK",
        "Αποδοχή όλων", "Αποδοχή", "Αποδέχομαι", "Συμφωνώ",
        // Legal/institutional-investor disclaimer gates (e.g. JPMorgan) often render their
        // accept control as a link or ARIA button rather than a plain <button>, with
        // wording like these instead of a generic "Accept".
        "Accept and Continue", "Enter Site", "I Understand", "I understand", "Confirm",
    ];

    private static async Task<string?> DismissOverlaysAsync(IPage page)
    {
        // Quantcast's consent dialog on Insider wraps its controls in a custom container
        // whose localized labels do not reliably match the generic text locators below.
        // Prefer invoking its own consent button; remove only the modal if it remains and
        // is still intercepting clicks, never any page/article content.
        try
        {
            var quantcastOverlay = page.Locator("#qc-cmp2-container");
            if (await quantcastOverlay.IsVisibleAsync(new() { Timeout = 500 }))
            {
                var acted = await page.EvaluateAsync<bool>("""
                    () => {
                        const overlay = document.querySelector('#qc-cmp2-container');
                        if (!overlay) return false;
                        const button = Array.from(overlay.querySelectorAll('button, [role="button"]'))
                            .find(element => /reject|decline|necessary|απόρριψη|απαραίτητα/i.test(element.textContent ?? ''))
                            ?? Array.from(overlay.querySelectorAll('button, [role="button"]'))
                                .find(element => /accept|agree|allow|αποδοχή|συμφωνώ/i.test(element.textContent ?? ''));
                        if (!button) return false;
                        (button instanceof HTMLElement ? button : null)?.click();
                        return true;
                    }
                    """);
                await page.WaitForTimeoutAsync(500);
                if (await quantcastOverlay.IsVisibleAsync(new() { Timeout = 500 }))
                    await quantcastOverlay.EvaluateAsync("element => element.remove()");
                return acted ? "Quantcast consent button" : "Quantcast overlay removed";
            }
        }
        catch { }

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

    private static async Task<string?> DismissJpmorganInstitutionalGateAsync(IPage page)
    {
        try
        {
            var result = await page.EvaluateAsync<string>("""
                () => {
                    const gateText = /institutional investor|professional client|important information|terms and conditions/i;
                    const actionText = /accept|agree|continue|confirm|enter site|i understand/i;
                    const containers = Array.from(document.querySelectorAll('[role="dialog"], [aria-modal="true"], [class*="disclaimer" i], [class*="modal" i], [class*="overlay" i]'));
                    const gate = containers.find(container => gateText.test(container.textContent ?? ''));
                    if (!gate) return 'not-found';
                    const action = Array.from(gate.querySelectorAll('button, a, [role="button"]'))
                        .find(element => actionText.test(element.textContent ?? ''));
                    if (!action || !(action instanceof HTMLElement)) return 'no-action';
                    action.click();
                    return `clicked:${(action.textContent ?? '').trim().slice(0, 80)}`;
                }
                """);
            if (result == "not-found")
                return null;

            await page.WaitForTimeoutAsync(750);
            return result == "no-action"
                ? "JPMorgan institutional gate found but no accept/continue control was available"
                : $"dismissed JPMorgan institutional gate via '{result[8..]}'";
        }
        catch
        {
            return "JPMorgan institutional gate dismissal could not be completed";
        }
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
                var buttons = page.Locator($"button:has-text('{text}'), a:has-text('{text}'), [role='button']:has-text('{text}'), [aria-expanded]:has-text('{text}')");
                var count = await buttons.CountAsync();
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var button = buttons.Nth(i);
                        if (await button.IsVisibleAsync(new() { Timeout = 500 }))
                        {
                            await ClickWithJsFallbackAsync(button);
                            if (string.Equals(await button.EvaluateAsync<string>("el => el.tagName"), "A", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 5000 });
                                }
                                catch (TimeoutException)
                                {
                                    // A same-page link may update client-side without a load event.
                                }
                            }
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
