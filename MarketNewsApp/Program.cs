using System.CommandLine;
using DotNetEnv;
using MarketNewsApp.Services;
using Scriban;

// Load .env file
Env.Load();

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

    // Step 1: Scrape
    Banner("Step 1/4 · Scraping financial sites with Playwright");
    var scraper = new Scraper();
    var scraped = scraper.ScrapeAllAsync().GetAwaiter().GetResult();
    var totalChars = scraped.Values.Sum(v => v.Text.Length);
    Console.WriteLine($"\n  ✅  Scraped {scraped.Count} sites · {totalChars:N0} chars total");

    // Step 2: AI summary + data extraction
    Banner("Step 2/4 · Generating Greek summary via Groq AI");
    var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY")
        ?? throw new InvalidOperationException("GROQ_API_KEY not set in environment");
    var summarizer = new AiSummarizer(apiKey);

    string aiHtml;
    MarketNewsApp.Models.MarketData marketData;
    try
    {
        (aiHtml, marketData) = summarizer.Run(scraped);
        Console.WriteLine("  ✅  AI summary ready");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌  AI summarizer failed: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    // Step 3: Generate charts
    Banner("Step 3/4 · Generating charts with ScottPlot");
    var chartGen = new ChartGenerator();
    var chartImages = chartGen.GenerateAll(marketData);
    Console.WriteLine($"  ✅  {chartImages.Count} charts generated");

    // Step 4: Send email (or save to file)
    if (dryRun)
    {
        Banner("Step 4/4 · DRY RUN — saving report.html (no email sent)");
        var emailSender = new EmailSender();
        var reportDate = DateTime.Now.ToString("dd/MM/yyyy");
        var sinceDate = DateTime.Now.AddDays(-10).ToString("dd/MM/yyyy");
        var html = emailSender.RenderHtml(aiHtml, chartImages, reportDate, sinceDate);

        // Replace cid: references with inline base64
        foreach (var (key, b64) in chartImages)
        {
            html = html.Replace($"cid:{key}", $"data:image/png;base64,{b64}");
            html = html.Replace($"cid:chart_{key}", $"data:image/png;base64,{b64}");
        }

        var outPath = Path.Combine(Directory.GetCurrentDirectory(), "report.html");
        File.WriteAllText(outPath, html);
        Console.WriteLine($"  Saved to {outPath}");
    }
    else
    {
        Banner("Step 4/4 · Sending email via Gmail SMTP");
        try
        {
            var emailSender = new EmailSender();
            emailSender.Send(aiHtml, chartImages);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  Email send failed: {ex.Message}");
            Environment.Exit(1);
        }
    }

    var elapsed = (DateTime.Now - start).TotalSeconds;
    Banner($"✅  Done in {elapsed:F0}s  —  {DateTime.Now:HH:mm:ss}", '═');
}
