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
}

public class ScrapedSite
{
    public required string Url { get; set; }
    public required string Text { get; set; }
}
