using System.Text.Json;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

public static class ScrapeCache
{
    private static string CacheDir =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "cache");

    private static string TodayFile =>
        Path.Combine(CacheDir, $"{DateTime.Now:yyyy-MM-dd}.json");

    public static bool TryLoad(out Dictionary<string, ScrapedSite> scraped)
    {
        scraped = [];
        try
        {
            var path = TodayFile;
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, ScrapedSite>>(json);
            if (data is { Count: > 0 }) { scraped = data; return true; }
        }
        catch { }
        return false;
    }

    public static void Save(Dictionary<string, ScrapedSite> scraped)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(TodayFile, JsonSerializer.Serialize(scraped));
            Console.WriteLine($"  💾  Cache saved → cache/{DateTime.Now:yyyy-MM-dd}.json");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Cache save failed: {ex.Message}");
        }
    }
}
