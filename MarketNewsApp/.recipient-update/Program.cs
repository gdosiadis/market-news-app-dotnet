using MarketNewsApp.Data;
using Microsoft.EntityFrameworkCore;

var databasePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "market-news.db"));
var options = new DbContextOptionsBuilder<MarketNewsDbContext>()
    .UseSqlite($"Data Source={databasePath}")
    .Options;

await using var database = new MarketNewsDbContext(options);
var address = "EChrysopoulou@optimabank.gr";
var recipient = await database.EmailRecipients.SingleOrDefaultAsync(item => item.Address == address);

if (recipient is null)
{
    database.EmailRecipients.Add(new EmailRecipient
    {
        Address = address,
        DisplayName = "E. Chrysopoulou",
        IsEnabled = true,
    });
}
else
{
    recipient.IsEnabled = true;
}

await database.SaveChangesAsync();

var activeRecipient = await database.EmailRecipients
    .AsNoTracking()
    .SingleAsync(item => item.Address == address && item.IsEnabled);

Console.WriteLine($"Enabled recipient: {activeRecipient.Address}");