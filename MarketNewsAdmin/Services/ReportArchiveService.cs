using MarketNewsAdmin.Models;

namespace MarketNewsAdmin.Services;

public sealed class ReportArchiveService(IConfiguration configuration, IWebHostEnvironment environment)
{
    public ReportArchiveViewModel GetReports()
    {
        var archivePath = ResolveArchivePath();
        if (!Directory.Exists(archivePath))
            return new ReportArchiveViewModel { ArchivePath = archivePath };

        var reports = Directory.EnumerateFiles(archivePath, "market-report-*.html")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select(file => new ArchivedReportViewModel(file.Name, file.CreationTimeUtc, file.Length))
            .ToList();
        return new ReportArchiveViewModel { ArchivePath = archivePath, Reports = reports };
    }

    public string? FindReportPath(string fileName)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) || !fileName.StartsWith("market-report-", StringComparison.Ordinal) || !fileName.EndsWith(".html", StringComparison.Ordinal))
            return null;

        var path = Path.Combine(ResolveArchivePath(), fileName);
        return File.Exists(path) ? path : null;
    }

    private string ResolveArchivePath()
    {
        var configuredPath = configuration["ReportArchive:Path"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "..", "MarketNewsApp", "reports")
            : configuredPath);
    }
}