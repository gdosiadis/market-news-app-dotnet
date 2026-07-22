using System.Text.Json;
using MarketNewsApp.Data;
using Microsoft.EntityFrameworkCore;

namespace MarketNewsAdmin.Services;

public sealed class AdminConfigurationService(IDbContextFactory<MarketNewsDbContext> contextFactory)
{
    public async Task<IReadOnlyList<ConfigurationAuditEntry>> HistoryAsync(string? entityType = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ConfigurationAuditEntries.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(entry => entry.EntityType == entityType);
        return (await query.ToListAsync(cancellationToken)).OrderByDescending(entry => entry.OccurredAt).Take(200).ToList();
    }

    public static void Audit(MarketNewsDbContext db, object entity, string action, string actor, string? before = null)
    {
        var entityType = entity.GetType().Name;
        var id = entity.GetType().GetProperty("Id")?.GetValue(entity)?.ToString() ?? "unknown";
        db.ConfigurationAuditEntries.Add(new ConfigurationAuditEntry
        {
            EntityType = entityType,
            EntityId = id,
            Action = action,
            Actor = actor,
            BeforeJson = before,
            AfterJson = action == "Deleted" ? null : JsonSerializer.Serialize(entity),
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }
}