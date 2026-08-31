using MarketNewsApp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MarketNewsApp.Services;

public static class ProductionMaintenance
{
    public static async Task RunAsync(DbContextOptions<MarketNewsDbContext> options, string connectionString)
    {
        var retentionDays = int.TryParse(Environment.GetEnvironmentVariable("RETENTION_DAYS"), out var configuredDays)
            ? Math.Max(configuredDays, 1)
            : 30;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        await using (var db = new MarketNewsDbContext(options))
        {
            var expiredCheckpoints = (await db.PipelineCheckpoints.ToListAsync())
                .Where(checkpoint => checkpoint.UpdatedAt < cutoff)
                .ToList();
            db.PipelineCheckpoints.RemoveRange(expiredCheckpoints);
            var deleted = await db.SaveChangesAsync();
            if (deleted > 0)
                Console.WriteLine($"  🧹 Removed {deleted} checkpoint(s) older than {retentionDays} days");
        }

        var archivePath = Environment.GetEnvironmentVariable("REPORT_ARCHIVE_PATH")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "reports");
        if (Directory.Exists(archivePath))
        {
            var deletedReports = 0;
            foreach (var report in Directory.EnumerateFiles(archivePath, "*.html"))
            {
                if (File.GetLastWriteTimeUtc(report) >= cutoff.UtcDateTime)
                    continue;
                File.Delete(report);
                deletedReports++;
            }
            if (deletedReports > 0)
                Console.WriteLine($"  🧹 Removed {deletedReports} report(s) older than {retentionDays} days");
        }

        var backupPath = Environment.GetEnvironmentVariable("BACKUP_PATH");
        if (string.IsNullOrWhiteSpace(backupPath))
            return;

        Directory.CreateDirectory(backupPath);
        var backupFile = Path.Combine(backupPath, $"market-news-{DateTime.UtcNow:yyyy-MM-dd}.db");
        if (File.Exists(backupFile))
            return;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO '{backupFile.Replace("'", "''", StringComparison.Ordinal)}'";
        await command.ExecuteNonQueryAsync();
        Console.WriteLine($"  💾 Database backup created → {backupFile}");
    }
}