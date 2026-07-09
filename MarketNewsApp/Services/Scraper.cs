using Microsoft.Playwright;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

public class Scraper
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    private static readonly SiteConfig[] Sites =
    [
        new()
        {
            Name = "Bloomberg Markets",
            Url = "https://www.bloomberg.com/markets",
            Selectors = ["article", "[data-component='headline']", "h1", "h2", "h3", ".story-package-module__headline"],
            WaitFor = "body",
            Timeout = 20000,
        },
        new()
        {
            Name = "BlackRock Investment Institute",
            Url = "https://www.blackrock.com/corporate/insights/blackrock-investment-institute/publications/weekly-commentary",
            Selectors = ["article", ".content-block", "h1", "h2", "p", ".editorial-content"],
            WaitFor = "body",
            Timeout = 25000,
        },
        new()
        {
            Name = "T. Rowe Price Global Markets",
            Url = "https://www.troweprice.com/personal-investing/resources/insights/global-markets-weekly-update.html",
            Selectors = ["article", "main", ".article-body", "h1", "h2", "h3", "p"],
            WaitFor = "main",
            Timeout = 25000,
        },
        new()
        {
            Name = "BNP Paribas AM Viewpoint",
            Url = "https://viewpoint.bnpparibas-am.com/",
            Selectors = ["article", "main", "p", ".article-title", "h1", "h2", "h3", ".card-title"],
            WaitFor = "body",
            Timeout = 20000,
        },
        new()
        {
            Name = "Edward Jones Weekly Update",
            Url = "https://www.edwardjones.com/us-en/market-news-insights/stock-market-news/stock-market-weekly-update",
            Selectors = ["article", "main", ".article-body", "h1", "h2", "h3", "p"],
            WaitFor = "main",
            Timeout = 25000,
        },
        new()
        {
            Name = "JPMorgan Weekly Market Recap",
            Url = "https://am.jpmorgan.com/us/en/asset-management/institutional/insights/market-insights/market-updates/weekly-market-recap/",
            Selectors = ["article", "main", ".content", "h1", "h2", "h3", "p"],
            WaitFor = "body",
            Timeout = 25000,
            // The real recap text is collapsed behind a "Read more" toggle by default;
            // expand it before extraction so we don't just scrape the legal disclaimer
            // gate and a truncated teaser.
            ExpandButtonTexts = ["Read more"],
            // JPMorgan keeps a static, server-rendered copy of the institutional-investor
            // disclaimer (".jp-seo-modal-container") in the DOM purely for SEO/crawlers —
            // it sits on top of (and intercepts clicks for) the real interactive gate, and
            // its duplicate legal text otherwise leaks into every selector match even
            // after the real gate is accepted.
            ExcludeSelectors = [".jp-seo-modal-container", ".jpm-am-overlay-disclaimer"],
        },
        new()
        {
            Name = "Citi Market Insights",
            Url = "https://marketinsights.citi.com/Market-Commentary/Weekly-Market-Update/index.html",
            Selectors = ["article", "main", ".content-area", "h1", "h2", "h3", "p"],
            WaitFor = "body",
            Timeout = 25000,
        },
    ];

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

        var tasks = Sites.Select(async site =>
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
            var hasCause = data.Diagnostics.Contains("CAUSE:", StringComparison.Ordinal);
            var ok = !data.Text.StartsWith("[") && !hasCause;
            Console.WriteLine($"  {(ok ? "OK" : "WARN")}  {name}  ({data.Url})");
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
                Text = string.IsNullOrWhiteSpace(cleanedText) ? $"[{site.Name}: no content extracted]" : cleanedText,
                Diagnostics = string.Join(" | ", diag)
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

    private static string CleanText(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 30)
            .Take(250);
        return string.Join("\n", lines);
    }

    // Best-effort dismissal of cookie/legal/terms consent overlays that block the real
    // content underneath (e.g. BlackRock's "accept Terms and Conditions" gate). Tries a
    // handful of common button labels; silently does nothing if none are found.
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
        foreach (var text in ConsentButtonTexts)
        {
            try
            {
                // Some sites render the accept control as a link or ARIA role="button"
                // <div>/<span> rather than a real <button> element (e.g. JPMorgan's
                // disclaimer gate), so check all three shapes, not just <button>.
                var button = page
                    .Locator($"button:has-text('{text}'), a:has-text('{text}'), [role='button']:has-text('{text}')")
                    .First;
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
