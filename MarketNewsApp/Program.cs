using System.CommandLine;
using System.Diagnostics;
using DotNetEnv;
using MarketNewsApp.Data;
using MarketNewsApp.Models;
using MarketNewsApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Scriban;

// Load .env file — search project directory first, then CWD
var envFile = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env");
Env.Load(File.Exists(envFile) ? envFile : ".env");

var connectionString = Environment.GetEnvironmentVariable("SQLITE_CONNECTION_STRING")
    ?? "Data Source=market-news.db";
var dbOptions = new DbContextOptionsBuilder<MarketNewsDbContext>().UseSqlite(connectionString).Options;
var configurationService = new ConfigurationService(new PooledDbContextFactory<MarketNewsDbContext>(dbOptions));
await using (var db = new MarketNewsDbContext(dbOptions))
    await db.Database.MigrateAsync();

var rootCommand = new RootCommand("Market News AI — Daily Email Report");

var nowOption = new Option<bool>("--now", "Run once and exit");
var testOption = new Option<bool>("--test", "Dry run — save HTML, no email");
var debugDomOption = new Option<string?>("--debug-dom", "Dump figure/chart element info for a URL and exit");

rootCommand.AddOption(nowOption);
rootCommand.AddOption(testOption);
rootCommand.AddOption(debugDomOption);

rootCommand.SetHandler(async (bool now, bool test, string? debugDomUrl) =>
{
    if (debugDomUrl is not null)
    {
        await DebugDomAsync(debugDomUrl);
        return;
    }

    if (test)
    {
        RunPipeline(configurationService.GetAsync().GetAwaiter().GetResult(), dryRun: true);
        return;
    }

    if (now)
    {
        RunPipeline(configurationService.GetAsync().GetAwaiter().GetResult(), dryRun: false);
        return;
    }

    // Scheduled mode
    Console.WriteLine("\n📅  Scheduler started — SQLite controls the daily send time");
    Console.WriteLine("    Press Ctrl+C to stop.\n");

    // Simple scheduler loop
    while (true)
    {
        var configuration = configurationService.GetAsync().GetAwaiter().GetResult();
        var sendTime = configuration.Schedule.DailySendTime;
        var now2 = DateTime.Now;
        var targetTime = TimeSpan.Parse(sendTime);
        if (configuration.Schedule.IsEnabled && now2.TimeOfDay.Hours == targetTime.Hours && now2.TimeOfDay.Minutes == targetTime.Minutes)
        {
            RunPipeline(configuration);
            Thread.Sleep(61000); // avoid re-trigger within the same minute
        }
        Thread.Sleep(30000);
    }
}, nowOption, testOption, debugDomOption);

return rootCommand.Invoke(args);

// ─────────────────────────────────────────────────────────────────────────────

static async Task DebugDomAsync(string url)
{
    using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new()
    {
        Headless = true,
        Args = ["--disable-blink-features=AutomationControlled"],
    });
    var context = await browser.NewContextAsync(new()
    {
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        ViewportSize = new Microsoft.Playwright.ViewportSize { Width = 1280, Height = 800 },
        Locale = "en-US",
    });
    var page = await context.NewPageAsync();
    await page.GotoAsync(url, new() { WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle, Timeout = 60000 });
    await page.WaitForTimeoutAsync(2000);

    // best-effort dismiss any cookie banner
    foreach (var text in new[] { "Reject All", "Accept All", "Accept", "Agree" })
    {
        try
        {
            var btn = page.Locator($"button:has-text('{text}')").First;
            if (await btn.IsVisibleAsync(new() { Timeout = 1000 })) { await btn.ClickAsync(); await page.WaitForTimeoutAsync(1000); break; }
        }
        catch { }
    }

    var shots = await MarketNewsApp.Services.Scraper.DebugCaptureScreenshotsAsync(page);
    Console.WriteLine($"Captured {shots.Count} screenshot(s)");
    Directory.CreateDirectory("debug_shots");
    for (var i = 0; i < shots.Count; i++)
    {
        File.WriteAllBytes($"debug_shots/shot_{i}.png", Convert.FromBase64String(shots[i]));
    }
}

static void Banner(string msg, char ch = '─')
{
    var line = new string(ch, 56);
    Console.WriteLine($"\n{line}\n  {msg}\n{line}");
}

static void PrintElapsed(string label, Stopwatch stopwatch) =>
    Console.WriteLine($"  ⏱️  {label}: {stopwatch.Elapsed.TotalSeconds:F1}s ({stopwatch.Elapsed:mm\\:ss})");

static void RunPipeline(RuntimeConfiguration configuration, bool dryRun = false)
{
    var start = DateTime.Now;
    var totalTimer = Stopwatch.StartNew();
    var stepTimer = Stopwatch.StartNew();
    Banner($"🚀  Market News AI  —  {start:dd/MM/yyyy HH:mm}", '═');

    // ── Step 1/5: Parallel Extraction ────────────────────────────────────────
    Banner("Step 1/5 · Parallel Extraction");
    Dictionary<string, ScrapedSite> scraped;
    Dictionary<string, ScrapedSite> cached = [];
    var fromCache = configuration.Features.GetValueOrDefault("scrape-cache") && ScrapeCache.TryLoad(out cached);
    if (fromCache)
    {
        scraped = cached;
        Console.WriteLine($"\n  💾  Cache hit — {scraped.Count} sites loaded (skipping scrape)");
    }
    else
    {
        var scraper = new Scraper(configuration.Sources);
        scraped = scraper.ScrapeAllAsync().GetAwaiter().GetResult();
        Console.WriteLine($"\n  ✅  Scraped {scraped.Count} sites · {scraped.Values.Sum(v => v.Text.Length):N0} chars · {scraped.Values.Sum(v => v.Screenshots.Count)} screenshots");
    }
    PrintElapsed("Step 1 extraction", stepTimer);

    // ── Step 2/5: Cleaning ───────────────────────────────────────────────────
    Banner("Step 2/5 · Cleaning — deduplication & normalization");
    stepTimer.Restart();
    var cleaned = AiSummarizer.CleanScraped(scraped);
    Console.WriteLine($"  ✅  {cleaned.Count} sites cleaned");
    AuditLogger.LogRun(scraped, cleaned, perSource: null, fromCache);
    PrintElapsed("Step 2 cleaning", stepTimer);

    // ── Step 3/5: Cache ──────────────────────────────────────────────────────
    Banner("Step 3/5 · Cache — persisting cleaned data");
    stepTimer.Restart();
    if (configuration.Features.GetValueOrDefault("scrape-cache")) ScrapeCache.Save(cleaned);
    PrintElapsed("Step 3 cache", stepTimer);

    // ── Step 4/5: Per-source AI summaries ────────────────────────────────────
    Banner("Step 4/5 · Per-source AI summaries (parallel)");
    stepTimer.Restart();
    var summaryCache = configuration.Features.GetValueOrDefault("summary-cache") ? SummaryCache.Load() : null;
    if (summaryCache != null)
        Console.WriteLine($"  💾  Same-day summary cache found — reusing unchanged sources, thinking only about new content");

    var summarizer = AiSummarizer.CreateAsync(configuration).GetAwaiter().GetResult();
    try
    {
        Dictionary<string, SourceSummary> perSource;
        try
        {
            perSource = summarizer.SummarizePerSourceAsync(cleaned, summaryCache?.PerSource).GetAwaiter().GetResult();
            Console.WriteLine($"  ✅  {perSource.Count} per-source summaries ready");
            PrintElapsed("Step 4 AI summaries", stepTimer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  Per-source summaries failed: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        // ── Step 5/5: Final synthesis + HTML template ─────────────────────────────
        Banner("Step 5/5 · Final synthesis + HTML template");
        stepTimer.Restart();

        var newPerSourceCache = cleaned.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var summary = perSource[kv.Key];
                var contentHash = SummaryCache.ComputeHash($"source-only-v4-retry-ai-failures\n{kv.Value.Text}");
                if (summary.Status is not (SourceStatus.Success or SourceStatus.Partial))
                    contentHash = SummaryCache.ComputeHash($"{contentHash}\n{summary.Status}");

                return new SummaryCache.SourceEntry(contentHash, summary.Html, summary.Status, summary.TranslatedContent);
            });
        var compositeHash = SummaryCache.ComputeCompositeHash(newPerSourceCache.Values.Select(v => v.ContentHash));

        string synthesis;
        if (summaryCache != null && summaryCache.CompositeHash == compositeHash && !string.IsNullOrWhiteSpace(summaryCache.Synthesis))
        {
            synthesis = summaryCache.Synthesis;
            Console.WriteLine("  ♻️  Synthesis reused from cache — no source content changed since last run today");
        }
        else
        {
            synthesis = summarizer.SynthesizeAsync(perSource).GetAwaiter().GetResult();
            Console.WriteLine("  ✅  Synthesis ready");
        }

        if (configuration.Features.GetValueOrDefault("summary-cache")) SummaryCache.Save(new SummaryCache.CachedRun(newPerSourceCache, compositeHash, synthesis));

        var srcList = string.Join("\n", cleaned.Select(kv =>
            $"<li>📄 <strong>{kv.Key}</strong> — <a href=\"{kv.Value.Url}\">{kv.Value.Url}</a></li>"));
        var aiHtml = summarizer.ComposeHtml(perSource, synthesis, srcList);

        var reportDateStr = DateTime.Now.ToString("dd/MM/yyyy");
        var sinceDateStr  = DateTime.Now.AddDays(-configuration.Report.LookbackDays).ToString("dd/MM/yyyy");

        if (dryRun)
        {
            var emailSender = new EmailSender(configuration.Email, configuration.ReportTemplate);
            var html        = emailSender.RenderHtml(aiHtml, reportDateStr, sinceDateStr);

            // Local file preview can't resolve cid: references (that's an email-client
            // mechanism) — inline the same screenshot bytes as data URIs instead so
            // report.html looks the same as what actually gets sent.
            foreach (var (sourceName, summary) in perSource)
            {
                for (var i = 0; i < summary.Screenshots.Count; i++)
                {
                    var cid = AiSummarizer.ScreenshotCid(sourceName, i);
                    html = html.Replace($"cid:{cid}", $"data:image/png;base64,{summary.Screenshots[i]}");
                }
            }

            var outPath = Path.Combine(Directory.GetCurrentDirectory(), "report.html");
            File.WriteAllText(outPath, html);
            Console.WriteLine($"  ✅  Saved to {outPath}");
        }
        else
        {
            try
            {
                var emailSender = new EmailSender(configuration.Email, configuration.ReportTemplate);
                emailSender.Send(aiHtml, perSource, configuration.EmailRecipients);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌  Email send failed: {ex.Message}");
                Environment.Exit(1);
            }
        }
        PrintElapsed("Step 5 synthesis and delivery", stepTimer);
    }
    finally
    {
        summarizer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    Banner($"✅  Done in {totalTimer.Elapsed.TotalSeconds:F0}s  —  {DateTime.Now:HH:mm:ss}", '═');
}
