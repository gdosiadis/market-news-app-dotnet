using Microsoft.Playwright;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

public class Scraper
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    private static readonly SiteConfig[] Sites =
    [
        new()
        {
            Name = "Bloomberg Markets",
            Url = "https://www.bloomberg.com/markets",
            Selectors = ["article", "[data-component='headline']", "h1", "h2", "h3", ".story-package-module__headline"],
            WaitFor = "body",
            Timeout = 20000,
        },
        new()
        {
            Name = "BlackRock Investment Institute",
            Url = "https://www.blackrock.com/corporate/insights/blackrock-investment-institute/publications/weekly-commentary",
            Selectors = ["article", ".content-block", "h1", "h2", "p", ".editorial-content"],
            WaitFor = "body",
            Timeout = 25000,
        },
        new()
        {
            Name = "T. Rowe Price Global Markets",
            Url = "https://www.troweprice.com/personal-investing/resources/insights/global-markets-weekly-update.html",
            Selectors = ["article", "main", ".article-body", "h1", "h2", "h3", "p"],
            WaitFor = "main",
            Timeout = 25000,
        },
        new()
        {
            Name = "John Hancock Weekly Recap",
            Url = "https://www.jhinvestments.com/weekly-market-recap",
            Selectors = ["article", "main", ".content", "h1", "h2", "h3", "p"],
            WaitFor = "body",
            Timeout = 25000,
        },
        new()
        {
            Name = "BNP Paribas AM Viewpoint",
            Url = "https://viewpoint.bnpparibas-am.com/",
            Selectors = ["article", ".article-title", "h1", "h2", "h3", ".card-title"],
            WaitFor = "body",
            Timeout = 20000,
        },
        new()
        {
            Name = "Edward Jones Weekly Update",
            Url = "https://www.edwardjones.com/us-en/market-news-insights/stock-market-news/stock-market-weekly-update",
            Selectors = ["article", "main", ".article-body", "h1", "h2", "h3", "p"],
            WaitFor = "main",
            Timeout = 25000,
        },
        new()
        {
            Name = "JPMorgan Weekly Market Recap",
            Url = "https://am.jpmorgan.com/us/en/asset-management/institutional/insights/market-insights/market-updates/weekly-market-recap/",
            Selectors = ["article", "main", ".content", "h1", "h2", "h3", "p"],
            WaitFor = "body",
            Timeout = 25000,
        },
        new()
        {
            Name = "Citi Market Insights",
            Url = "https://marketinsights.citi.com/Market-Commentary/Weekly-Market-Update/index.html",
            Selectors = ["article", "main", ".content-area", "h1", "h2", "h3", "p"],
            WaitFor = "body",
            Timeout = 25000,
        },
    ];

    public async Task<Dictionary<string, ScrapedSite>> ScrapeAllAsync()
    {
        var results = new Dictionary<string, ScrapedSite>();
        using var semaphore = new SemaphoreSlim(3);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });

        var tasks = Sites.Select(async site =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await ScrapeSiteAsync(browser, site);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var completedResults = await Task.WhenAll(tasks);
        foreach (var (name, data) in completedResults)
        {
            results[name] = data;
            var ok = !data.Text.StartsWith("[");
            Console.WriteLine($"  {(ok ? "OK" : "WARN")}  {name}  ({data.Url})");
        }

        return results;
    }

    private async Task<(string Name, ScrapedSite Data)> ScrapeSiteAsync(IBrowser browser, SiteConfig site)
    {
        var context = await browser.NewContextAsync(new()
        {
            UserAgent = UserAgent,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            Locale = "en-US",
        });

        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(site.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = site.Timeout });

            try
            {
                await page.WaitForSelectorAsync(site.WaitFor, new() { Timeout = 8000 });
            }
            catch (TimeoutException) { }

            await page.WaitForTimeoutAsync(3000);

            var parts = new List<string>();
            foreach (var selector in site.Selectors)
            {
                try
                {
                    var elements = await page.QuerySelectorAllAsync(selector);
                    foreach (var el in elements.Take(50))
                    {
                        var text = await el.InnerTextAsync();
                        if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length > 20)
                            parts.Add(text.Trim());
                    }
                }
                catch { }
            }

            string cleanedText;
            if (parts.Count > 0)
                cleanedText = CleanText(string.Join("\n", parts));
            else
                cleanedText = CleanText(await page.InnerTextAsync("body"));

            return (site.Name, new ScrapedSite
            {
                Url = site.Url,
                Text = string.IsNullOrWhiteSpace(cleanedText) ? $"[{site.Name}: no content extracted]" : cleanedText
            });
        }
        catch (TimeoutException)
        {
            return (site.Name, new ScrapedSite { Url = site.Url, Text = $"[{site.Name}: page load timed out]" });
        }
        catch (Exception ex)
        {
            return (site.Name, new ScrapedSite { Url = site.Url, Text = $"[{site.Name}: error — {ex.Message}]" });
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static string CleanText(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 30)
            .Take(250);
        return string.Join("\n", lines);
    }
}
