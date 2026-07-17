using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

public static class ScrapeCache
{
    private static string CacheDir =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "cache");

    private static string TodayFile =>
        Path.Combine(CacheDir, $"{DateTime.Now:yyyy-MM-dd}.json");

    private static string TodayCompressedDir =>
        Path.Combine(CacheDir, "compressed", DateTime.Now.ToString("yyyy-MM-dd"));

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

            SaveCompressedArchives(scraped);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Cache save failed: {ex.Message}");
        }
    }

    private static void SaveCompressedArchives(Dictionary<string, ScrapedSite> scraped)
    {
        try
        {
            Directory.CreateDirectory(TodayCompressedDir);
            foreach (var (sourceName, site) in scraped)
            {
                var archivePath = Path.Combine(TodayCompressedDir, $"{SafeFileName(sourceName)}.zip");
                if (File.Exists(archivePath))
                    File.Delete(archivePath);

                using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
                var contentEntry = archive.CreateEntry("scraped-content.txt", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(contentEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.WriteLine($"Πηγή: {sourceName}");
                    writer.WriteLine($"URL: {site.Url}");
                    writer.WriteLine($"Αποθήκευση: {DateTime.Now:O}");
                    writer.WriteLine();
                    writer.Write(site.Text);
                }

                var metadataEntry = archive.CreateEntry("metadata.json", CompressionLevel.Optimal);
                using var metadataWriter = new StreamWriter(metadataEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                metadataWriter.Write(JsonSerializer.Serialize(new
                {
                    sourceName,
                    site.Url,
                    savedAt = DateTime.Now,
                    site.Diagnostics,
                    screenshotCount = site.Screenshots.Count,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }

            Console.WriteLine($"  🗜️  Readable archives saved → cache/compressed/{DateTime.Now:yyyy-MM-dd}/");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Compressed archive save failed: {ex.Message}");
        }
    }

    private static string SafeFileName(string sourceName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return string.Concat(sourceName.Select(character => invalidCharacters.Contains(character) ? '_' : character));
    }
}
