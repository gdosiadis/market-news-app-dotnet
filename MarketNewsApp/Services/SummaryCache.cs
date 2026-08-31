using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

/// <summary>
/// Caches the AI-generated per-source summaries and the final synthesis for the day,
/// so that re-running the pipeline the same day doesn't re-burn expensive AI calls on
/// content that hasn't changed. Only sources whose cleaned text actually changed since
/// the last run are re-summarized; the synthesis itself is only regenerated when at
/// least one source changed. Everything else is reused as-is.
/// </summary>
public static class SummaryCache
{
    public record SourceEntry(string ContentHash, string Html, SourceStatus Status, string? TranslatedContent = null);
    public record CachedRun(Dictionary<string, SourceEntry> PerSource, string CompositeHash, string Synthesis);

    private static string CacheDir =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "cache");

    private static string TodayFile =>
        Path.Combine(CacheDir, $"{DateTime.Now:yyyy-MM-dd}-summary.json");

    public static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")))[..16];

    public static string ComputeCompositeHash(IEnumerable<string> contentHashes) =>
        ComputeHash(string.Join("|", contentHashes.OrderBy(h => h, StringComparer.Ordinal)));

    public static CachedRun? Load()
    {
        try
        {
            var path = TodayFile;
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CachedRun>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(CachedRun run)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(TodayFile, JsonSerializer.Serialize(run));
            Console.WriteLine($"  💾  Summary cache saved → cache/{DateTime.Now:yyyy-MM-dd}-summary.json");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Summary cache save failed: {ex.Message}");
        }
    }

    public static void ClearToday()
    {
        if (File.Exists(TodayFile))
            File.Delete(TodayFile);
    }
}
