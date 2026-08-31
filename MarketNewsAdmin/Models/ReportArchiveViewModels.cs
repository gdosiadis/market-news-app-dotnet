namespace MarketNewsAdmin.Models;

public sealed class ReportArchiveViewModel
{
    public IReadOnlyList<ArchivedReportViewModel> Reports { get; init; } = [];
    public string ArchivePath { get; init; } = "";
}

public sealed record ArchivedReportViewModel(string FileName, DateTimeOffset CreatedAt, long SizeBytes);