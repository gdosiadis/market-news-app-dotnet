using System.Text.Json;
using MarketNewsApp.Data;
using MarketNewsApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MarketNewsApp.Services;

public sealed class PipelineCheckpointStore(DbContextOptions<MarketNewsDbContext> options)
{
    private const string ScrapeStage = "scrape";
    private const string SummaryStage = "summary";
    private static readonly JsonSerializerOptions JsonOptions = new();

    public async Task<Dictionary<string, ScrapedSite>> LoadScrapedAsync(IEnumerable<string> sourceNames, CancellationToken cancellationToken = default)
    {
        var names = sourceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var db = new MarketNewsDbContext(options);
        var checkpoints = await db.PipelineCheckpoints.AsNoTracking()
            .Where(item => item.RunDate == Today && item.Stage == ScrapeStage && names.Contains(item.SourceName))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, ScrapedSite>(StringComparer.OrdinalIgnoreCase);
        foreach (var checkpoint in checkpoints)
        {
            try
            {
                var site = JsonSerializer.Deserialize<ScrapedSite>(checkpoint.PayloadJson, JsonOptions);
                if (site is { IsOk: true }) result[checkpoint.SourceName] = site;
            }
            catch (JsonException) { }
        }
        return result;
    }

    public async Task<Dictionary<string, ScrapedSite>> LoadPreviousDayScrapedAsync(IEnumerable<string> sourceNames, CancellationToken cancellationToken = default)
    {
        var names = sourceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var db = new MarketNewsDbContext(options);
        var checkpoints = await db.PipelineCheckpoints.AsNoTracking()
            .Where(item => item.RunDate == PreviousDay && item.Stage == ScrapeStage && names.Contains(item.SourceName))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, ScrapedSite>(StringComparer.OrdinalIgnoreCase);
        foreach (var checkpoint in checkpoints.OrderByDescending(item => item.UpdatedAt))
        {
            if (result.ContainsKey(checkpoint.SourceName))
                continue;

            try
            {
                var site = JsonSerializer.Deserialize<ScrapedSite>(checkpoint.PayloadJson, JsonOptions);
                if (site is { IsOk: true }) result[checkpoint.SourceName] = site;
            }
            catch (JsonException) { }
        }
        return result;
    }

    public Task SaveScrapedAsync(string runId, string sourceName, ScrapedSite site, CancellationToken cancellationToken = default) =>
        SaveAsync(runId, ScrapeStage, sourceName, null, site, cancellationToken);

    public async Task<Dictionary<string, SummaryCache.SourceEntry>> LoadSummariesAsync(
        Dictionary<string, ScrapedSite> cleaned,
        CancellationToken cancellationToken = default)
    {
        await using var db = new MarketNewsDbContext(options);
        var checkpoints = await db.PipelineCheckpoints.AsNoTracking()
            .Where(item => item.RunDate == Today && item.Stage == SummaryStage)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, SummaryCache.SourceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var checkpoint in checkpoints)
        {
            if (!cleaned.TryGetValue(checkpoint.SourceName, out var site)) continue;
            var expectedHash = SummaryCache.ComputeHash($"source-only-v4-retry-ai-failures\n{site.Text}");
            if (!string.Equals(checkpoint.ContentHash, expectedHash, StringComparison.Ordinal)) continue;

            try
            {
                var entry = JsonSerializer.Deserialize<SummaryCache.SourceEntry>(checkpoint.PayloadJson, JsonOptions);
                if (entry is { Status: SourceStatus.Success or SourceStatus.Partial }) result[checkpoint.SourceName] = entry;
            }
            catch (JsonException) { }
        }
        return result;
    }

    public Task SaveSummaryAsync(string runId, string sourceName, string contentHash, SummaryCache.SourceEntry entry, CancellationToken cancellationToken = default) =>
        SaveAsync(runId, SummaryStage, sourceName, contentHash, entry, cancellationToken);

    public async Task DeleteForSourcesAsync(IEnumerable<string> sourceNames, CancellationToken cancellationToken = default)
    {
        var names = sourceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
            return;

        await using var db = new MarketNewsDbContext(options);
        var checkpoints = await db.PipelineCheckpoints
            .Where(item => item.RunDate == Today && names.Contains(item.SourceName))
            .ToListAsync(cancellationToken);
        db.PipelineCheckpoints.RemoveRange(checkpoints);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveAsync(string runId, string stage, string sourceName, string? contentHash, object payload, CancellationToken cancellationToken)
    {
        await using var db = new MarketNewsDbContext(options);
        var checkpoint = await db.PipelineCheckpoints.FindAsync([runId, stage, sourceName], cancellationToken);
        if (checkpoint is null)
        {
            checkpoint = new PipelineCheckpoint { RunId = runId, RunDate = Today, Stage = stage, SourceName = sourceName, PayloadJson = "{}", UpdatedAt = DateTimeOffset.UtcNow };
            db.PipelineCheckpoints.Add(checkpoint);
        }
        checkpoint.ContentHash = contentHash;
        checkpoint.PayloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        checkpoint.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");
    private static string PreviousDay => DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
}