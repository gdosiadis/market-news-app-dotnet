namespace MarketNewsAdmin.Models;

public sealed class PipelineRunsViewModel
{
    public IReadOnlyList<PipelineRunSummary> Runs { get; init; } = [];
    public string LogPath { get; init; } = "";
    public bool LogAvailable { get; init; }
}

public sealed record PipelineConsoleViewModel(bool IsRunning, int? ExitCode, IReadOnlyList<string> Lines);

public sealed record PipelineRunSummary(
    string RunId,
    DateTimeOffset StartedAt,
    int SourceCount,
    int ScrapeFailures,
    int AiFailures,
    string SynthesisStatus,
    bool FromCache,
    IReadOnlyList<PipelineSourceEvent> Events);

public sealed record PipelineSourceEvent(
    string Site,
    string Url,
    DateTimeOffset OccurredAt,
    bool ScrapeIsOk,
    string AiStatus,
    string SynthesisStatus,
    string Diagnostics);

public sealed class PipelineCheckpointsViewModel
{
    public IReadOnlyList<PipelineCheckpointViewModel> Checkpoints { get; init; } = [];
}

public sealed record PipelineCheckpointViewModel(
    string RunId,
    string RunDate,
    string Stage,
    string SourceName,
    string? ContentHash,
    DateTimeOffset UpdatedAt);