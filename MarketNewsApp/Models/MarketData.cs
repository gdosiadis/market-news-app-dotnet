namespace MarketNewsApp.Models;

public class MarketData
{
    public Dictionary<string, IndexData> Indices { get; set; } = new();
    public Dictionary<string, double> Yields { get; set; } = new();
    public Dictionary<string, double> Forex { get; set; } = new();
    public Dictionary<string, double> Commodities { get; set; } = new();
    public Dictionary<string, double> Macro { get; set; } = new();
}

public class IndexData
{
    public double WeeklyPct { get; set; }
    public double YtdPct { get; set; }
}

public class SiteConfig
{
    public required string Name { get; set; }
    public required string Url { get; set; }
    public required string[] Selectors { get; set; }
    public required string WaitFor { get; set; }
    public int Timeout { get; set; } = 20000;

    // Extra settle time (ms) after the page hydrates, on top of the base 3s wait.
    // Sites with aggressive bot-detection (e.g. Akamai) sometimes need a longer,
    // more human-like warm-up before they release the real content.
    public int ExtraSettleMs { get; set; } = 0;

    // Button texts to click before extraction to expand collapsed/truncated content
    // (e.g. JPMorgan's "Read more" toggle that hides the actual recap text by default).
    public string[] ExpandButtonTexts { get; set; } = [];

    // CSS selectors for elements to strip from the DOM before extraction — used for
    // duplicate/noise content that isn't a real overlay to dismiss (e.g. JPMorgan keeps a
    // static, SSR-rendered copy of its institutional-investor disclaimer around for SEO
    // crawlers even after the interactive gate is accepted, and it leaks into every
    // selector-based extraction otherwise).
    public string[] ExcludeSelectors { get; set; } = [];
}

public class ScrapedSite
{
    public required string Url { get; set; }
    public required string Text { get; set; }

    // Human-readable explanation of what happened during scraping (HTTP status,
    // detected block signatures, overlay-dismissal outcome, content stats, etc.).
    // Populated by Scraper regardless of success/failure so failures are diagnosable
    // without re-running with extra logging.
    public string Diagnostics { get; set; } = "";

    // True when the scrape actually produced usable content. Failed scrapes store a
    // "[Site: reason]"-style placeholder in Text and/or a "CAUSE:" marker in Diagnostics —
    // shared by the live scraper's console reporting and the daily cache validity check so
    // a transient failure (e.g. a one-off page-load timeout) doesn't get "frozen" as a
    // false negative for the rest of the day.
    public bool IsOk =>
        !Text.StartsWith('[') && !Diagnostics.Contains("CAUSE:", StringComparison.Ordinal);
}

public enum SourceStatus
{
    Success,        // Full analysis produced from ample content
    Partial,        // Analysis produced but from limited content
    Blocked,        // No content retrieved (scrape failed / access blocked)
    DisclaimerOnly, // AI produced only a disclaimer / near-empty output
    Error,          // AI call threw an exception
}

public record SourceSummary(string Html, SourceStatus Status, string Url);
