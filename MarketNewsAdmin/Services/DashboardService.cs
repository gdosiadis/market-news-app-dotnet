using MarketNewsAdmin.Models;
using MarketNewsApp.Data;
using Microsoft.EntityFrameworkCore;

namespace MarketNewsAdmin.Services;

public sealed class DashboardService(IDbContextFactory<MarketNewsDbContext> contextFactory)
{
    public async Task<DashboardViewModel> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return new DashboardViewModel
        {
            EnabledSources = await db.ScrapeSources.CountAsync(source => source.IsEnabled, cancellationToken),
            TotalSources = await db.ScrapeSources.CountAsync(cancellationToken),
            ActiveFlags = await db.FeatureFlags.CountAsync(flag => flag.IsEnabled, cancellationToken),
            EnabledRecipients = await db.EmailRecipients.CountAsync(recipient => recipient.IsEnabled, cancellationToken),
            NextSchedule = (await db.SchedulingSettings.AsNoTracking().SingleAsync(cancellationToken)).DailySendTime,
            RecentActivity = (await db.ConfigurationAuditEntries.AsNoTracking().ToListAsync(cancellationToken)).OrderByDescending(entry => entry.OccurredAt).Take(8).ToList(),
        };
    }
}