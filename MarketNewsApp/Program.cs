using System.CommandLine;
using DotNetEnv;
using MarketNewsApp.Models;
using MarketNewsApp.Services;
using Scriban;

// Load .env file — search project directory first, then CWD
var envFile = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env");
Env.Load(File.Exists(envFile) ? envFile : ".env");

var rootCommand = new RootCommand("Market News AI — Daily Email Report");

var nowOption = new Option<bool>("--now", "Run once and exit");
var testOption = new Option<bool>("--test", "Dry run — save HTML, no email");

rootCommand.AddOption(nowOption);
rootCommand.AddOption(testOption);

rootCommand.SetHandler((bool now, bool test) =>
{
    if (test)
    {
        RunPipeline(dryRun: true);
        return;
    }

    if (now)
    {
        RunPipeline(dryRun: false);
        return;
    }

    // Scheduled mode
    var sendTime = Environment.GetEnvironmentVariable("SEND_TIME") ?? "07:00";
    Console.WriteLine($"\n📅  Scheduler started — report will be sent daily at {sendTime}");
    Console.WriteLine("    Press Ctrl+C to stop.\n");

    // Run immediately on first launch
    RunPipeline();

    // Simple scheduler loop
    while (true)
    {
        var now2 = DateTime.Now;
        var targetTime = TimeSpan.Parse(sendTime);
        if (now2.TimeOfDay.Hours == targetTime.Hours && now2.TimeOfDay.Minutes == targetTime.Minutes)
        {
            RunPipeline();
            Thread.Sleep(61000); // avoid re-trigger within the same minute
        }
        Thread.Sleep(30000);
    }
}, nowOption, testOption);

return rootCommand.Invoke(args);

// ─────────────────────────────────────────────────────────────────────────────

static void Banner(string msg, char ch = '─')
{
    var line = new string(ch, 56);
    Console.WriteLine($"\n{line}\n  {msg}\n{line}");
}

static void RunPipeline(bool dryRun = false)
{
    var start = DateTime.Now;
    Banner($"🚀  Market News AI  —  {start:dd/MM/yyyy HH:mm}", '═');

    // ── Step 1/6: Parallel Extraction ────────────────────────────────────────
    Banner("Step 1/6 · Parallel Extraction");
    Dictionary<string, ScrapedSite> scraped;
    if (ScrapeCache.TryLoad(out var cached))
    {
        scraped = cached;
        Console.WriteLine($"\n  💾  Cache hit — {scraped.Count} sites loaded (skipping scrape)");
    }
    else
    {
        var scraper = new Scraper();
        scraped = scraper.ScrapeAllAsync().GetAwaiter().GetResult();
        Console.WriteLine($"\n  ✅  Scraped {scraped.Count} sites · {scraped.Values.Sum(v => v.Text.Length):N0} chars");
    }

    // ── Step 2/6: Cleaning ───────────────────────────────────────────────────
    Banner("Step 2/6 · Cleaning — deduplication & normalization");
    var cleaned = AiSummarizer.CleanScraped(scraped);
    Console.WriteLine($"  ✅  {cleaned.Count} sites cleaned");

    // ── Step 3/6: Cache ──────────────────────────────────────────────────────
    Banner("Step 3/6 · Cache — persisting cleaned data");
    ScrapeCache.Save(cleaned);

    // ── Step 4/6: Per-source AI summaries ────────────────────────────────────
    Banner("Step 4/6 · Per-source AI summaries (parallel)");
    var summarizer = new AiSummarizer();
    try
    {
        Dictionary<string, SourceSummary> perSource;
        try
        {
            perSource = summarizer.SummarizePerSourceAsync(cleaned).GetAwaiter().GetResult();
            Console.WriteLine($"  ✅  {perSource.Count} per-source summaries ready");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  Per-source summaries failed: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        // ── Step 5/6: Final synthesis + market data (parallel) ───────────────────
        Banner("Step 5/6 · Final synthesis + market data extraction (parallel)");
        var synthesisTask = summarizer.SynthesizeAsync(perSource);
        var dataTask      = summarizer.ExtractMarketDataAsync(cleaned);
        Task.WhenAll(synthesisTask, dataTask).GetAwaiter().GetResult();
        var synthesis  = synthesisTask.Result;
        var marketData = dataTask.Result;
        Console.WriteLine("  ✅  Synthesis and market data ready");

        // ── Step 6/6: Charts + HTML template ─────────────────────────────────────
        Banner("Step 6/6 · Charts + HTML template");
        var chartGen    = new ChartGenerator();
        var chartImages = chartGen.GenerateAll(marketData);
        Console.WriteLine($"  ✅  {chartImages.Count} charts generated");

        var srcList = string.Join("\n", cleaned.Select(kv =>
            $"<li>📄 <strong>{kv.Key}</strong> — <a href=\"{kv.Value.Url}\">{kv.Value.Url}</a></li>"));
        var aiHtml = AiSummarizer.ComposeHtml(perSource, synthesis, srcList);

        var reportDateStr = DateTime.Now.ToString("dd/MM/yyyy");
        var sinceDateStr  = DateTime.Now.AddDays(-10).ToString("dd/MM/yyyy");

        // ── PowerPoint export — same data as the email, in slide form ─────────
        var pptxPath = Path.Combine(Directory.GetCurrentDirectory(), "report.pptx");
        try
        {
            new PptxReportGenerator().Generate(pptxPath, perSource, synthesis, chartImages, reportDateStr, sinceDateStr);
            Console.WriteLine($"  ✅  PowerPoint saved to {pptxPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  PowerPoint generation failed: {ex.Message}");
            pptxPath = null!;
        }

        if (dryRun)
        {
            var emailSender = new EmailSender();
            var html        = emailSender.RenderHtml(aiHtml, chartImages, reportDateStr, sinceDateStr);

            foreach (var (key, b64) in chartImages)
            {
                html = html.Replace($"cid:{key}", $"data:image/png;base64,{b64}");
                html = html.Replace($"cid:chart_{key}", $"data:image/png;base64,{b64}");
            }

            var outPath = Path.Combine(Directory.GetCurrentDirectory(), "report.html");
            File.WriteAllText(outPath, html);
            Console.WriteLine($"  ✅  Saved to {outPath}");
        }
        else
        {
            try
            {
                var emailSender = new EmailSender();
                emailSender.Send(aiHtml, chartImages, pptxPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌  Email send failed: {ex.Message}");
                Environment.Exit(1);
            }
        }
    }
    finally
    {
        summarizer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    var elapsed = (DateTime.Now - start).TotalSeconds;
    Banner($"✅  Done in {elapsed:F0}s  —  {DateTime.Now:HH:mm:ss}", '═');
}
