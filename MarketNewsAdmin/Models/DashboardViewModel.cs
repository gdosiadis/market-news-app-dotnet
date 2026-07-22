using MarketNewsApp.Data;

namespace MarketNewsAdmin.Models;

public sealed class DashboardViewModel
{
    public int EnabledSources { get; init; }
    public int TotalSources { get; init; }
    public int ActiveFlags { get; init; }
    public int EnabledRecipients { get; init; }
    public string NextSchedule { get; init; } = "--:--";
    public IReadOnlyList<ConfigurationAuditEntry> RecentActivity { get; init; } = [];
}