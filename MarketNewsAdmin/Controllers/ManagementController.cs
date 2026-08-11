using System.Text.Json;
using MarketNewsAdmin.Models;
using MarketNewsAdmin.Services;
using MarketNewsApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketNewsAdmin.Controllers;

[Authorize(Policy = "Administrators")]
public sealed class ManagementController(IDbContextFactory<MarketNewsDbContext> contextFactory, AdminConfigurationService auditService) : Controller
{
    private static readonly ISet<string> SingletonSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agents", "schedules" };
    private static readonly ISet<string> SourceSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sources", "greek-sources" };
    private static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sources"] = "International Sources", ["greek-sources"] = "Greek Sources", ["prompts"] = "Prompts", ["agents"] = "Agents", ["schedules"] = "Schedules",
        ["recipients"] = "Email Recipients", ["flags"] = "Feature Flags", ["templates"] = "Report Templates", ["settings"] = "Application Settings",
    };

    public async Task<IActionResult> Index(string section = "sources", string? search = null)
    {
        if (!Titles.ContainsKey(section)) return NotFound();
        await using var db = await contextFactory.CreateDbContextAsync();
        var rows = await RowsAsync(db, section, search);
        return View(new ManagementListViewModel { Section = section, Title = Titles[section], Search = search, CanCreate = !SingletonSections.Contains(section), Rows = rows });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string section, int? id)
    {
        if (!Titles.ContainsKey(section)) return NotFound();
        if (id is null && SingletonSections.Contains(section)) return RedirectToAction(nameof(Index), new { section });
        await using var db = await contextFactory.CreateDbContextAsync();
        var model = id is null ? NewForm(section) : await ExistingFormAsync(db, section, id.Value);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ConfigurationFormViewModel model)
    {
        if (!Titles.ContainsKey(model.Section)) return NotFound();
        if (model.Section == "agents" && string.IsNullOrWhiteSpace(model.Secondary))
        {
            ModelState.Remove(nameof(model.Secondary));
            model.Secondary = "";
        }
        var validationError = ValidateForm(model);
        if (validationError is not null)
            ModelState.AddModelError(validationError.Value.Key, validationError.Value.Message);
        if (!ModelState.IsValid) return View(model);
        await using var db = await contextFactory.CreateDbContextAsync();
        var actor = User.Identity?.Name ?? "unknown";
        var (entity, action, before) = await ApplyAsync(db, model);
        await db.SaveChangesAsync();
        AdminConfigurationService.Audit(db, entity, action, actor, before);
        await db.SaveChangesAsync();
        TempData["Success"] = $"{Titles[model.Section]} saved.";
        return RedirectToAction(nameof(Index), new { section = model.Section });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string section, int id)
    {
        if (!Titles.ContainsKey(section)) return NotFound();
        if (SingletonSections.Contains(section)) return BadRequest("This runtime configuration record must be updated, not deleted.");
        await using var db = await contextFactory.CreateDbContextAsync();
        var entity = await FindAsync(db, section, id);
        if (entity is null) return NotFound();
        var before = JsonSerializer.Serialize(entity);
        db.Remove(entity);
        await db.SaveChangesAsync();
        AdminConfigurationService.Audit(db, entity, "Deleted", User.Identity?.Name ?? "unknown", before);
        await db.SaveChangesAsync();
        TempData["Success"] = $"{Titles[section]} entry deleted.";
        return RedirectToAction(nameof(Index), new { section });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleSource(string section, int id)
    {
        if (!SourceSections.Contains(section)) return NotFound();
        await using var db = await contextFactory.CreateDbContextAsync();
        var source = await db.ScrapeSources.FindAsync(id);
        if (source is null || source.SourceRegion != SourceRegionFor(section)) return NotFound();

        var before = JsonSerializer.Serialize(source);
        source.IsEnabled = !source.IsEnabled;
        await db.SaveChangesAsync();
        AdminConfigurationService.Audit(db, source, "Updated", User.Identity?.Name ?? "unknown", before);
        await db.SaveChangesAsync();
        TempData["Success"] = $"{source.Name} is now {(source.IsEnabled ? "selected for scraping" : "excluded from scraping")}.";
        return RedirectToAction(nameof(Index), new { section });
    }

    public async Task<IActionResult> Activity(string? entityType = null) => View(new ActivityViewModel { EntityType = entityType, Entries = await auditService.HistoryAsync(entityType) });

    private static async Task<IReadOnlyList<ManagementRow>> RowsAsync(MarketNewsDbContext db, string section, string? search) => section switch
    {
        "sources" => await SourceRowsAsync(db, "International", search),
        "greek-sources" => await SourceRowsAsync(db, "Greek", search),
        "prompts" => await db.Prompts.AsNoTracking().Where(item => search == null || item.Key.Contains(search)).OrderBy(item => item.Key).Select(item => new ManagementRow(item.Id, item.Key, item.Template, item.IsEnabled)).ToListAsync(),
        "agents" => await db.AgentSettings.AsNoTracking().Select(item => new ManagementRow(item.Id, item.Provider, item.CopilotModel ?? "Default model", null)).ToListAsync(),
        "schedules" => await db.SchedulingSettings.AsNoTracking().Select(item => new ManagementRow(item.Id, item.DailySendTime, "Daily report schedule", item.IsEnabled)).ToListAsync(),
        "recipients" => await db.EmailRecipients.AsNoTracking().Where(item => search == null || item.Address.Contains(search)).OrderBy(item => item.Address).Select(item => new ManagementRow(item.Id, item.Address, item.DisplayName ?? "No display name", item.IsEnabled)).ToListAsync(),
        "flags" => await db.FeatureFlags.AsNoTracking().Where(item => search == null || item.Key.Contains(search)).OrderBy(item => item.Key).Select(item => new ManagementRow(item.Id, item.Key, "Runtime feature switch", item.IsEnabled)).ToListAsync(),
        "templates" => await db.ReportTemplates.AsNoTracking().Where(item => search == null || item.Name.Contains(search)).OrderByDescending(item => item.IsDefault).Select(item => new ManagementRow(item.Id, item.Name, item.SubjectTemplate, item.IsEnabled)).ToListAsync(),
        "settings" => await db.ApplicationSettings.AsNoTracking().Where(item => search == null || item.Key.Contains(search)).OrderBy(item => item.Key).Select(item => new ManagementRow(item.Id, item.Key, item.Value, null)).ToListAsync(),
        _ => [],
    };

    private static Task<List<ManagementRow>> SourceRowsAsync(MarketNewsDbContext db, string sourceRegion, string? search) =>
        db.ScrapeSources.AsNoTracking().Where(item => item.SourceRegion == sourceRegion && (search == null || item.Name.Contains(search) || item.Url.Contains(search))).OrderBy(item => item.SortOrder).Select(item => new ManagementRow(item.Id, item.Name, item.Url, item.IsEnabled)).ToListAsync();

    private static ConfigurationFormViewModel NewForm(string section) => new() { Section = section, Primary = "", Secondary = "", IsEnabled = true, Number = SourceSections.Contains(section) ? 20000 : 0, SourceRegion = SourceRegionFor(section) };
    private static async Task<ConfigurationFormViewModel?> ExistingFormAsync(MarketNewsDbContext db, string section, int id) => section switch
    {
        "sources" or "greek-sources" => await db.ScrapeSources.FindAsync(id) is { } item && item.SourceRegion == SourceRegionFor(section) ? new() { Section = section, Id = item.Id, Primary = item.Name, Secondary = item.Url, Tertiary = item.SelectorsJson, Detail = item.WaitFor, Number = item.TimeoutMs, SourceRegion = item.SourceRegion, IsEnabled = item.IsEnabled } : null,
        "prompts" => await db.Prompts.FindAsync(id) is { } item ? new() { Section = section, Id = item.Id, Primary = item.Key, Secondary = item.Template, IsEnabled = item.IsEnabled } : null,
        "agents" => await db.AgentSettings.FindAsync(id) is { } item ? new() { Section = section, Id = item.Id, Primary = item.Provider, Secondary = item.CopilotModel ?? "", Tertiary = item.AzureEndpoint, Detail = item.AzureDeployment, IsEnabled = true } : null,
        "schedules" => await db.SchedulingSettings.FindAsync(id) is { } item ? new() { Section = section, Id = item.Id, Primary = item.DailySendTime, Secondary = "Daily schedule", IsEnabled = item.IsEnabled } : null,
        "recipients" => await db.EmailRecipients.FindAsync(id) is { } item ? new() { Section = section, Id = item.Id, Primary = item.Address, Secondary = item.DisplayName ?? "", IsEnabled = item.IsEnabled } : null,
        "flags" => await db.FeatureFlags.FindAsync(id) is { } item ? new() { Section = section, Id = item.Id, Primary = item.Key, Secondary = "Runtime feature switch", IsEnabled = item.IsEnabled } : null,
        "templates" => await db.ReportTemplates.FindAsync(id) is { } item ? new() { Section = section, Id = item.Id, Primary = item.Name, Secondary = item.SubjectTemplate, Tertiary = item.BodyTemplate, IsEnabled = item.IsEnabled } : null,
        "settings" => await db.ApplicationSettings.FindAsync(id) is { } item ? new() { Section = section, Id = item.Id, Primary = item.Key, Secondary = item.Value, Tertiary = item.Description, IsEnabled = true } : null,
        _ => null,
    };

    private static async Task<object?> FindAsync(MarketNewsDbContext db, string section, int id) => section switch
    {
        "sources" or "greek-sources" => await db.ScrapeSources.FindAsync(id), "prompts" => await db.Prompts.FindAsync(id), "agents" => await db.AgentSettings.FindAsync(id), "schedules" => await db.SchedulingSettings.FindAsync(id), "recipients" => await db.EmailRecipients.FindAsync(id), "flags" => await db.FeatureFlags.FindAsync(id), "templates" => await db.ReportTemplates.FindAsync(id), "settings" => await db.ApplicationSettings.FindAsync(id), _ => null,
    };

    private static (string Key, string Message)? ValidateForm(ConfigurationFormViewModel model)
    {
        if (SourceSections.Contains(model.Section) && !Uri.TryCreate(model.Secondary, UriKind.Absolute, out _)) return ("Secondary", "Enter a valid source URL.");
        if (model.Section == "recipients" && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(model.Primary)) return ("Primary", "Enter a valid email address.");
        if (model.Section == "schedules" && !TimeOnly.TryParse(model.Primary, out _)) return ("Primary", "Use a 24-hour time, for example 07:00.");
        return null;
    }

    private static async Task<(object Entity, string Action, string? Before)> ApplyAsync(MarketNewsDbContext db, ConfigurationFormViewModel form)
    {
        var existing = form.Id is null ? null : await FindAsync(db, form.Section, form.Id.Value);
        var action = existing is null ? "Created" : "Updated";
        var before = existing is null ? null : JsonSerializer.Serialize(existing);
        object entity = form.Section switch
        {
            "sources" or "greek-sources" => existing as ScrapeSourceConfiguration ?? new ScrapeSourceConfiguration { Name = form.Primary, Url = form.Secondary, SelectorsJson = form.Tertiary ?? "[]", WaitFor = form.Detail ?? "body", SourceRegion = SourceRegionFor(form.Section) },
            "prompts" => existing as PromptConfiguration ?? new PromptConfiguration { Key = form.Primary, Template = form.Secondary },
            "agents" => existing as AgentConfiguration ?? new AgentConfiguration { Provider = form.Primary },
            "schedules" => existing as SchedulingConfiguration ?? new SchedulingConfiguration { DailySendTime = form.Primary },
            "recipients" => existing as EmailRecipient ?? new EmailRecipient { Address = form.Primary },
            "flags" => existing as FeatureFlag ?? new FeatureFlag { Key = form.Primary },
            "templates" => existing as ReportTemplateConfiguration ?? new ReportTemplateConfiguration { Name = form.Primary, SubjectTemplate = form.Secondary, BodyTemplate = form.Tertiary ?? "" },
            "settings" => existing as ApplicationSetting ?? new ApplicationSetting { Key = form.Primary, Value = form.Secondary },
            _ => throw new InvalidOperationException(),
        };
        switch (entity)
        {
            case ScrapeSourceConfiguration item: item.Name = form.Primary; item.Url = form.Secondary; item.SelectorsJson = form.Tertiary ?? "[]"; item.WaitFor = form.Detail ?? "body"; item.TimeoutMs = form.Number; item.SourceRegion = SourceRegionFor(form.Section); item.IsEnabled = form.IsEnabled; item.SortOrder = item.SortOrder == 0 ? item.Id : item.SortOrder; break;
            case PromptConfiguration item: item.Key = form.Primary; item.Template = form.Secondary; item.IsEnabled = form.IsEnabled; break;
            case AgentConfiguration item: item.Provider = form.Primary; item.CopilotModel = string.IsNullOrWhiteSpace(form.Secondary) ? null : form.Secondary.Trim(); item.AzureEndpoint = form.Tertiary; item.AzureDeployment = form.Detail; break;
            case SchedulingConfiguration item: item.DailySendTime = form.Primary; item.IsEnabled = form.IsEnabled; break;
            case EmailRecipient item: item.Address = form.Primary; item.DisplayName = form.Secondary; item.IsEnabled = form.IsEnabled; break;
            case FeatureFlag item: item.Key = form.Primary; item.IsEnabled = form.IsEnabled; break;
            case ReportTemplateConfiguration item: item.Name = form.Primary; item.SubjectTemplate = form.Secondary; item.BodyTemplate = form.Tertiary ?? ""; item.IsEnabled = form.IsEnabled; break;
            case ApplicationSetting item: item.Key = form.Primary; item.Value = form.Secondary; item.Description = form.Tertiary; break;
        }
        if (existing is null) db.Add(entity);
        return (entity, action, before);
    }

    private static string SourceRegionFor(string section) => section == "greek-sources" ? "Greek" : "International";
}