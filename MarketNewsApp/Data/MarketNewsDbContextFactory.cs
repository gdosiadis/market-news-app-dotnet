using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MarketNewsApp.Data;

public sealed class MarketNewsDbContextFactory : IDesignTimeDbContextFactory<MarketNewsDbContext>
{
    public MarketNewsDbContext CreateDbContext(string[] args)
    {
        var envFile = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (File.Exists(envFile)) Env.Load(envFile);
        var connectionString = Environment.GetEnvironmentVariable("SQLITE_CONNECTION_STRING")
            ?? "Data Source=market-news.db";
        return new MarketNewsDbContext(new DbContextOptionsBuilder<MarketNewsDbContext>()
            .UseSqlite(connectionString)
            .Options);
    }
}