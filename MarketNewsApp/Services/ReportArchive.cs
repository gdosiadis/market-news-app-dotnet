using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

public static class ReportArchive
{
    public static void Save(string html, IReadOnlyDictionary<string, SourceSummary> summaries)
    {
        try
        {
            var previewHtml = html;
            foreach (var (sourceName, summary) in summaries)
            {
                for (var index = 0; index < summary.Screenshots.Count; index++)
                {
                    var cid = AiSummarizer.ScreenshotCid(sourceName, index);
                    previewHtml = previewHtml.Replace($"cid:{cid}", $"data:image/png;base64,{summary.Screenshots[index]}", StringComparison.Ordinal);
                }
            }

            var archiveDirectory = Environment.GetEnvironmentVariable("REPORT_ARCHIVE_PATH")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "reports");
            Directory.CreateDirectory(archiveDirectory);
            var fileName = $"market-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.html";
            File.WriteAllText(Path.Combine(archiveDirectory, fileName), previewHtml);
            Console.WriteLine($"  Report archived → {Path.Combine(archiveDirectory, fileName)}");
        }
        catch (Exception exception)
        {
            // The email was already accepted by SMTP; archive failure must not turn that send into a retry risk.
            Console.WriteLine($"  ⚠️  Report archive failed: {exception.Message}");
        }
    }
}