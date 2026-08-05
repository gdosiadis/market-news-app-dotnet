using System.ComponentModel.DataAnnotations;
using MarketNewsApp.Data;

namespace MarketNewsAdmin.Models;

public sealed class ManagementListViewModel
{
    public required string Section { get; init; }
    public required string Title { get; init; }
    public string? Search { get; init; }
    public bool CanCreate { get; init; } = true;
    public IReadOnlyList<ManagementRow> Rows { get; init; } = [];
}

public sealed record ManagementRow(int Id, string Primary, string Secondary, bool? Enabled);

public sealed class ConfigurationFormViewModel
{
    public required string Section { get; set; }
    public int? Id { get; set; }
    [Required, StringLength(200)] public string Primary { get; set; } = "";
    [Required] public string Secondary { get; set; } = "";
    public string? Tertiary { get; set; }
    public string? Detail { get; set; }
    public int Number { get; set; }
    public string SourceRegion { get; set; } = "International";
    public bool IsEnabled { get; set; } = true;
}

public sealed class ActivityViewModel
{
    public string? EntityType { get; init; }
    public IReadOnlyList<ConfigurationAuditEntry> Entries { get; init; } = [];
}