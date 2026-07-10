using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

// Records a structured, append-only audit trail of every scrape/AI run so scraped content
// can be reviewed or diffed after the fact — without re-running with a debugger attached.
//
// Writes three artifacts under logs/:
//   - audit.jsonl        one JSON line per site per run (status, lengths, hash, diagnostics)
//   - last-hashes.json   most recent content hash per site, used to flag day-over-day
//                        "unchanged" content (a common sign of a stale/cached page that
//                        didn't actually refresh, rather than a genuinely quiet news day)
//   - raw/{date}/*.txt   the raw pre-clean scraped text for each site, archived only on
//                        days with a fresh scrape (not a cache hit), so the exact text the
//                        scraper saw can be diffed against the live page by hand.
public static class AuditLogger
{
    private static string LogsDir =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "logs");

    private static string AuditLogFile => Path.Combine(LogsDir, "audit.jsonl");
    private static string LastHashesFile => Path.Combine(LogsDir, "last-hashes.json");
    private static string RawArchiveDir(string date) => Path.Combine(LogsDir, "raw", date);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private record LastHashEntry(string Date, string Hash);

    private record AuditRecord(
        string RunId,
        string Timestamp,
        string Site,
        string Url,
        bool FromCache,
        int RawLength,
        int CleanedLength,
        string ContentHash,
        bool ScrapeIsOk,
        string ScrapeDiagnostics,
        string AiStatus,
        bool? ChangedSincePreviousDay);

    /// <param name="rawScraped">Pre-clean scraped data (or the cached dict, when fromCache is true).</param>
    /// <param name="cleaned">Post-clean data actually sent to the AI / cached for the day.</param>
    /// <param name="perSource">Per-source AI classification, once available (Step 4).</param>
    /// <param name="fromCache">True when Step 1 was a cache hit (no fresh scrape occurred).</param>
    public static void LogRun(
        Dictionary<string, ScrapedSite> rawScraped,
        Dictionary<string, ScrapedSite> cleaned,
        Dictionary<string, SourceSummary>? perSource,
        bool fromCache)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var lastHashes = LoadLastHashes();
            var records = new List<AuditRecord>();

            foreach (var (name, cleanedSite) in cleaned)
            {
                var rawLen = rawScraped.TryGetValue(name, out var raw) ? raw.Text.Length : cleanedSite.Text.Length;
                var hash = ComputeHash(cleanedSite.Text);

                bool? changed = null;
                if (lastHashes.TryGetValue(name, out var prev) && prev.Date != today)
                    changed = prev.Hash != hash;
                lastHashes[name] = new LastHashEntry(today, hash);

                var aiStatus = perSource != null && perSource.TryGetValue(name, out var summary)
                    ? summary.Status.ToString()
                    : "n/a";

                records.Add(new AuditRecord(
                    runId, DateTime.Now.ToString("O"), name, cleanedSite.Url, fromCache,
                    rawLen, cleanedSite.Text.Length, hash,
                    cleanedSite.IsOk, cleanedSite.Diagnostics, aiStatus, changed));
            }

            using (var writer = new StreamWriter(AuditLogFile, append: true))
            {
                foreach (var r in records)
                    writer.WriteLine(JsonSerializer.Serialize(r, JsonOpts));
            }

            SaveLastHashes(lastHashes);

            // Archive raw pre-clean text only when there was an actual fresh scrape —
            // a cache hit has no new raw text to archive (it's the previously-cleaned data).
            if (!fromCache)
            {
                var dir = RawArchiveDir(today);
                Directory.CreateDirectory(dir);
                foreach (var (name, site) in rawScraped)
                {
                    var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
                    File.WriteAllText(Path.Combine(dir, $"{safeName}.txt"), site.Text);
                }
            }

            var unchanged = records.Where(r => r.ChangedSincePreviousDay == false).Select(r => r.Site).ToList();
            if (unchanged.Count > 0)
                Console.WriteLine($"  ⚠️  Unchanged vs. previous scrape — possible stale/cached page: {string.Join(", ", unchanged)}");

            Console.WriteLine($"  📝  Audit log appended → logs/audit.jsonl ({records.Count} entries, run {runId})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Audit logging failed: {ex.Message}");
        }
    }

    private static Dictionary<string, LastHashEntry> LoadLastHashes()
    {
        try
        {
            if (File.Exists(LastHashesFile))
            {
                var json = File.ReadAllText(LastHashesFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, LastHashEntry>>(json);
                if (data != null) return data;
            }
        }
        catch { }
        return [];
    }

    private static void SaveLastHashes(Dictionary<string, LastHashEntry> hashes)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            File.WriteAllText(LastHashesFile, JsonSerializer.Serialize(hashes, JsonOpts));
        }
        catch { }
    }

    // Short SHA-256 prefix — enough entropy to detect content changes without bloating the log.
    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
