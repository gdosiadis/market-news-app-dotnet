using MarketNewsApp.Data;
using Microsoft.EntityFrameworkCore;

var databasePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "market-news.db"));
var options = new DbContextOptionsBuilder<MarketNewsDbContext>()
    .UseSqlite($"Data Source={databasePath}")
    .Options;

await using var database = new MarketNewsDbContext(options);
var source = await database.ScrapeSources.SingleAsync(item => item.Name == "BNP Paribas AM Viewpoint");
source.FollowFirstLinkSelector = ".bnpvp-card-entry h2 a";
await database.SaveChangesAsync();

var updated = await database.ScrapeSources
    .AsNoTracking()
    .SingleAsync(item => item.Name == "BNP Paribas AM Viewpoint");
Console.WriteLine($"Updated FollowFirstLinkSelector for {updated.Name}: {updated.FollowFirstLinkSelector}");