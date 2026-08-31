using MarketNewsApp.Data;
using MarketNewsApp.Models;
using MarketNewsApp.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketNewsApp.Tests;

public sealed class PipelineCheckpointStoreTests
{
    [Fact]
    public async Task ProductionMaintenance_removes_only_expired_checkpoints()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MarketNewsDbContext>().UseSqlite(connection).Options;
        await using (var db = new MarketNewsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.PipelineCheckpoints.AddRange(
                Checkpoint("expired", "2026-08-19", "Capital", "expired content", DateTimeOffset.UtcNow.AddDays(-2)),
                Checkpoint("current", "2026-08-21", "Citi", "current content", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var originalRetentionDays = Environment.GetEnvironmentVariable("RETENTION_DAYS");
        try
        {
            Environment.SetEnvironmentVariable("RETENTION_DAYS", "1");
            await ProductionMaintenance.RunAsync(options, connection.ConnectionString);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RETENTION_DAYS", originalRetentionDays);
        }

        await using var verificationDb = new MarketNewsDbContext(options);
        var remaining = await verificationDb.PipelineCheckpoints.SingleAsync();
        Assert.Equal("current", remaining.RunId);
    }

    [Fact]
    public async Task LoadPreviousDayScrapedAsync_returns_latest_successful_checkpoint_per_source()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MarketNewsDbContext>().UseSqlite(connection).Options;
        await using (var db = new MarketNewsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
            db.PipelineCheckpoints.AddRange(
                Checkpoint("older", yesterday, "Capital", "older content", DateTimeOffset.UtcNow.AddDays(-1).AddHours(-1)),
                Checkpoint("latest", yesterday, "Capital", "latest content", DateTimeOffset.UtcNow.AddDays(-1)),
                Checkpoint("failed", yesterday, "Citi", "[Citi: timed out]", DateTimeOffset.UtcNow.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        var store = new PipelineCheckpointStore(options);
        var checkpoints = await store.LoadPreviousDayScrapedAsync(["Capital", "Citi"]);

        Assert.Single(checkpoints);
        Assert.Equal("latest content", checkpoints["Capital"].Text);
    }

    private static PipelineCheckpoint Checkpoint(string runId, string runDate, string sourceName, string text, DateTimeOffset updatedAt) => new()
    {
        RunId = runId,
        RunDate = runDate,
        Stage = "scrape",
        SourceName = sourceName,
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ScrapedSite { Url = "https://example.test", Text = text }),
        UpdatedAt = updatedAt,
    };
}