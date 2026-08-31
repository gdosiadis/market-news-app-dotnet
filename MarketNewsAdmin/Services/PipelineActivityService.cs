using System.Text.Json;
using MarketNewsAdmin.Models;

namespace MarketNewsAdmin.Services;

public sealed class PipelineActivityService(IConfiguration configuration, IWebHostEnvironment environment)
{
    private const int MaximumRuns = 100;

    public async Task<PipelineRunsViewModel> GetRunsAsync(CancellationToken cancellationToken = default)
    {
        var logPath = ResolveLogPath();
        if (!File.Exists(logPath))
            return new PipelineRunsViewModel { LogPath = logPath };

        var events = new List<PipelineLogRecord>();
        try
        {
            using var reader = File.OpenText(logPath);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<PipelineLogRecord>(line);
                    if (record is not null) events.Add(record);
                }
                catch (JsonException)
                {
                    // A partially written log line must not hide earlier completed events.
                }
            }
        }
        catch (IOException)
        {
            return new PipelineRunsViewModel { LogPath = logPath };
        }

        var runs = events
            .GroupBy(item => item.RunId)
            .Select(group =>
            {
                var sourceEvents = group.OrderBy(item => item.Timestamp).Select(item => new PipelineSourceEvent(
                    item.Site, item.Url, item.Timestamp, item.ScrapeIsOk, item.AiStatus, item.SynthesisStatus ?? "NotRun", item.ScrapeDiagnostics)).ToList();
                var synthesisStatus = sourceEvents.FirstOrDefault()?.SynthesisStatus ?? "NotRun";
                return new PipelineRunSummary(
                    group.Key,
                    sourceEvents.MinBy(item => item.OccurredAt)!.OccurredAt,
                    sourceEvents.Count,
                    sourceEvents.Count(item => !item.ScrapeIsOk),
                    sourceEvents.Count(item => !string.Equals(item.AiStatus, "Success", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.AiStatus, "n/a", StringComparison.OrdinalIgnoreCase)),
                    synthesisStatus,
                    group.All(item => item.FromCache),
                    sourceEvents);
            })
            .OrderByDescending(item => item.StartedAt)
            .Take(MaximumRuns)
            .ToList();

        return new PipelineRunsViewModel { Runs = runs, LogPath = logPath, LogAvailable = true };
    }

    private string ResolveLogPath()
    {
        var configuredPath = configuration["PipelineLogs:Path"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "..", "MarketNewsApp", "logs", "audit.jsonl")
            : configuredPath);
    }

    private sealed record PipelineLogRecord(
        string RunId,
        DateTimeOffset Timestamp,
        string Site,
        string Url,
        bool FromCache,
        bool ScrapeIsOk,
        string ScrapeDiagnostics,
        string AiStatus,
        string? SynthesisStatus);
}