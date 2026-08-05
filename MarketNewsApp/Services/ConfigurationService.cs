using System.Collections.Concurrent;
using System.Text.Json;
using MarketNewsApp.Data;
using MarketNewsApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MarketNewsApp.Services;

public sealed record RuntimeConfiguration(
    IReadOnlyList<SiteConfig> Sources,
    IReadOnlyDictionary<string, string> Prompts,
    EmailConfiguration Email,
    IReadOnlyList<string> EmailRecipients,
    SchedulingConfiguration Schedule,
    AgentConfiguration Agent,
    ReportConfiguration Report,
    IReadOnlyDictionary<string, bool> Features,
    ReportTemplateConfiguration? ReportTemplate,
    IReadOnlyDictionary<string, string> Settings);

public interface IConfigurationService
{
    Task<RuntimeConfiguration> GetAsync(CancellationToken cancellationToken = default);
    void Invalidate();
}

public sealed class ConfigurationService(IDbContextFactory<MarketNewsDbContext> contextFactory) : IConfigurationService
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private RuntimeConfiguration? _cached;
    private DateTimeOffset _cacheExpiresAt;

    public async Task<RuntimeConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
            return _cached;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
                return _cached;

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var settings = (await db.ApplicationSettings.AsNoTracking().ToListAsync(cancellationToken))
                .ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.OrdinalIgnoreCase);
            var configuration = new RuntimeConfiguration(
                (await db.ScrapeSources.AsNoTracking().Where(source => source.IsEnabled).OrderBy(source => source.SortOrder).ToListAsync(cancellationToken)).Select(ToSiteConfig).ToList(),
                (await db.Prompts.AsNoTracking().Where(prompt => prompt.IsEnabled).ToListAsync(cancellationToken)).ToDictionary(prompt => prompt.Key, prompt => prompt.Template, StringComparer.OrdinalIgnoreCase),
                await db.EmailSettings.AsNoTracking().SingleAsync(cancellationToken),
                await db.EmailRecipients.AsNoTracking().Where(recipient => recipient.IsEnabled).OrderBy(recipient => recipient.Address).Select(recipient => recipient.Address).ToListAsync(cancellationToken),
                await db.SchedulingSettings.AsNoTracking().SingleAsync(cancellationToken),
                await db.AgentSettings.AsNoTracking().SingleAsync(cancellationToken),
                await db.ReportSettings.AsNoTracking().SingleAsync(cancellationToken),
                (await db.FeatureFlags.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(flag => flag.Key, flag => flag.IsEnabled, StringComparer.OrdinalIgnoreCase),
                await db.ReportTemplates.AsNoTracking().Where(template => template.IsEnabled).OrderByDescending(template => template.IsDefault).FirstOrDefaultAsync(cancellationToken),
                settings);
            _cached = configuration;
            _cacheExpiresAt = DateTimeOffset.UtcNow.Add(CacheDuration(settings));
            return configuration;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate() => _cacheExpiresAt = DateTimeOffset.MinValue;

    private static TimeSpan CacheDuration(IReadOnlyDictionary<string, string> settings) =>
        settings.TryGetValue("configuration-cache-minutes", out var value) &&
        int.TryParse(value, out var minutes) && minutes is >= 0 and <= 60
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(5);

    private static SiteConfig ToSiteConfig(ScrapeSourceConfiguration source) => new()
    {
        Name = source.Name,
        Url = source.Url,
        SourceRegion = source.SourceRegion,
        Selectors = Deserialize(source.SelectorsJson),
        WaitFor = source.WaitFor,
        Timeout = source.TimeoutMs,
        ExtraSettleMs = source.ExtraSettleMs,
        ExpandButtonTexts = Deserialize(source.ExpandButtonTextsJson),
        ExcludeSelectors = Deserialize(source.ExcludeSelectorsJson),
        ScreenshotSelectors = Deserialize(source.ScreenshotSelectorsJson),
        FollowFirstLinkSelector = source.FollowFirstLinkSelector,
    };

    private static string[] Deserialize(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : JsonSerializer.Deserialize<string[]>(value) ?? [];
}