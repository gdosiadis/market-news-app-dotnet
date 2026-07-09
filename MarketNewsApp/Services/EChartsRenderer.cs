using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;

namespace MarketNewsApp.Services;

// Renders Apache ECharts option objects to PNG bytes headlessly, using the project's existing
// Playwright/Chromium dependency (already required for web scraping) — this adds zero new
// runtime dependencies. echarts.min.js is bundled as an embedded resource (Assets/echarts.min.js)
// so rendering works fully offline with no CDN/network access at runtime, which matters in a
// bank environment. ECharts was chosen over ScottPlot for chart types (dual-axis combo charts,
// horizontal "range + dot" dumbbell charts, forecast-divider annotations) where it produces
// noticeably more polished, editorial-quality output matching the reference report decks.
public sealed class EChartsRenderer : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private static string? _echartsJs;

    private async Task EnsureBrowserAsync()
    {
        if (_browser is not null) return;
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    // Renders the given ECharts "option" object (a plain Dictionary/POCO that serializes to the
    // ECharts option schema) at the requested pixel size and returns the chart as a base64 PNG.
    public async Task<string> RenderBase64Async(object option, int width, int height)
    {
        await EnsureBrowserAsync();

        var page = await _browser!.NewPageAsync(new()
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });

        try
        {
            var optionJson = JsonSerializer.Serialize(option);
            var html = $$"""
                <!DOCTYPE html>
                <html>
                <head>
                <meta charset="utf-8">
                <script>{{LoadEchartsJs()}}</script>
                <style>html,body{margin:0;padding:0;background:#ffffff;}</style>
                </head>
                <body>
                <div id="c" style="width:{{width}}px;height:{{height}}px"></div>
                <script>
                  var chart = echarts.init(document.getElementById('c'), null, { renderer: 'canvas' });
                  chart.setOption({{optionJson}});
                </script>
                </body>
                </html>
                """;

            await page.SetContentAsync(html);
            await page.WaitForFunctionAsync("() => document.querySelector('#c canvas') !== null");
            // Small settle delay so ECharts finishes its render pass (labels/animations) before capture.
            await page.WaitForTimeoutAsync(200);

            var bytes = await page.Locator("#c").ScreenshotAsync(new() { Type = ScreenshotType.Png });
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static string LoadEchartsJs()
    {
        if (_echartsJs is not null) return _echartsJs;
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames().First(n => n.EndsWith("echarts.min.js"));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        _echartsJs = reader.ReadToEnd();
        return _echartsJs;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}
