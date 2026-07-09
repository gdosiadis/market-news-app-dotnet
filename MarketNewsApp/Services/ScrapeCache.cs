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
            if (data is not { Count: > 0 }) return false;

            // Don't let a transient failure (e.g. a one-off page-load timeout) get "frozen"
            // as the day's result: if any cached site failed, treat the whole cache as stale
            // so the run falls through to a fresh scrape instead of reusing a bad result for
            // the rest of the day.
            var failed = data.Where(kv => !kv.Value.IsOk).Select(kv => kv.Key).ToList();
            if (failed.Count > 0)
            {
                Console.WriteLine($"  ⚠️  Cache has {failed.Count} failed site(s) ({string.Join(", ", failed)}) — ignoring cache, re-scraping");
                return false;
            }

            scraped = data;
            return true;
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
