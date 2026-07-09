using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

// Builds the report's chart PNGs using Apache ECharts (rendered headlessly via Playwright —
// see EChartsRenderer). Colors mirror the Optima Bank brand palette (navy/orange/gold) and the
// chart idioms mirror the reference "Weekly Supportive material" deck: a horizontal
// "range + dot" chart for indices (weekly-change bar + YTD diamond marker), and labeled bar
// charts for the other categories.
public class ChartGenerator
{
    private const string White = "#FFFFFF";
    private const string Navy = "#1B1B3A";
    private const string Orange = "#FF8B00";
    private const string Gold = "#E8B84B";
    private const string Teal = "#2E7D8C";
    private const string Red = "#C0392B";
    private const string Green = "#2E8B57";
    private const string TextColor = "#1A1A1A";
    private const string GridColor = "#D9D9D9";

    private const int ChartWidth = 900;
    private const int ChartHeight = 560;

    public async Task<Dictionary<string, string>> GenerateAllAsync(MarketData data)
    {
        var charts = new Dictionary<string, string>();
        await using var renderer = new EChartsRenderer();

        await SafeGenerateAsync(charts, "indices", () => ChartIndicesAsync(renderer, data.Indices));
        await SafeGenerateAsync(charts, "yields", () => ChartYieldsAsync(renderer, data.Yields));
        await SafeGenerateAsync(charts, "forex", () => ChartForexAsync(renderer, data.Forex));
        await SafeGenerateAsync(charts, "macro", () => ChartMacroAsync(renderer, data.Macro));
        await SafeGenerateAsync(charts, "commodities", () => ChartCommoditiesAsync(renderer, data.Commodities));

        return charts;
    }

    private static async Task SafeGenerateAsync(Dictionary<string, string> charts, string name, Func<Task<string?>> generator)
    {
        try
        {
            var result = await generator();
            if (!string.IsNullOrEmpty(result))
            {
                charts[name] = result;
                Console.WriteLine($"  📊  Chart ready: {name}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Chart failed ({name}): {ex.Message}");
        }
    }

    // Base option shared by every chart: white background, brand text color, no title (the
    // slide's own header/caption handles that), generous grid margins for axis/category labels.
    private static Dictionary<string, object> BaseOption(object grid) => new()
    {
        ["backgroundColor"] = White,
        ["animation"] = false,
        ["textStyle"] = new Dictionary<string, object> { ["color"] = TextColor, ["fontFamily"] = "Segoe UI, Arial, sans-serif" },
        ["grid"] = grid,
    };

    // "Assets in review" style: a horizontal weekly-performance bar (green/red by sign) per
    // index, plus a gold diamond marker showing the YTD return — mirrors the reference deck's
    // range+dot chart instead of a plain grouped bar comparison.
    private static async Task<string?> ChartIndicesAsync(EChartsRenderer renderer, Dictionary<string, IndexData> indices)
    {
        if (indices.Count == 0) return null;

        // Sort by weekly performance so the strongest movers read top-to-bottom, like the reference.
        var ordered = indices.OrderByDescending(kv => kv.Value.WeeklyPct).ToArray();
        var names = ordered.Select(kv => kv.Key).ToArray();
        var weekly = ordered.Select(kv => kv.Value.WeeklyPct).ToArray();
        var ytd = ordered.Select(kv => kv.Value.YtdPct).ToArray();

        var barData = weekly.Select(v => new Dictionary<string, object>
        {
            ["value"] = v,
            ["itemStyle"] = new Dictionary<string, object> { ["color"] = v >= 0 ? Green : Red },
            ["label"] = new Dictionary<string, object>
            {
                ["show"] = true,
                ["position"] = v >= 0 ? "right" : "left",
                ["formatter"] = FormatSigned(v),
                ["fontWeight"] = "bold",
                ["color"] = TextColor,
            },
        }).ToArray();

        // YTD marker overlay (the "dot" half of the range+dot idiom) — positioned against the
        // same category axis via [value, categoryIndex] pairs.
        var scatterData = ytd.Select((v, i) => new object[] { v, i }).ToArray();

        var option = BaseOption(new Dictionary<string, object> { ["left"] = 110, ["right"] = 60, ["top"] = 20, ["bottom"] = 55 });
        option["xAxis"] = new Dictionary<string, object>
        {
            ["type"] = "value",
            ["name"] = "Εβδομαδιαία απόδοση (%)",
            ["nameLocation"] = "middle",
            ["nameGap"] = 32,
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
            ["splitLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["yAxis"] = new Dictionary<string, object>
        {
            ["type"] = "category",
            ["data"] = names,
            ["inverse"] = true, // keep names[0] (best performer) at the top
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["legend"] = new Dictionary<string, object> { ["data"] = new[] { "YTD %" }, ["right"] = 20, ["bottom"] = 0 };
        option["series"] = new object[]
        {
            new Dictionary<string, object>
            {
                ["type"] = "bar",
                ["data"] = barData,
                ["barWidth"] = "55%",
                ["markLine"] = new Dictionary<string, object>
                {
                    ["silent"] = true,
                    ["symbol"] = "none",
                    ["lineStyle"] = new Dictionary<string, object> { ["type"] = "dashed", ["color"] = GridColor },
                    ["label"] = new Dictionary<string, object> { ["show"] = false },
                    ["data"] = new object[] { new Dictionary<string, object> { ["xAxis"] = 0 } },
                },
            },
            new Dictionary<string, object>
            {
                ["type"] = "scatter",
                ["name"] = "YTD %",
                ["data"] = scatterData,
                ["symbol"] = "diamond",
                ["symbolSize"] = 20,
                ["itemStyle"] = new Dictionary<string, object>
                {
                    ["color"] = Gold,
                    ["borderColor"] = Navy,
                    ["borderWidth"] = 1.5,
                },
                ["label"] = new Dictionary<string, object>
                {
                    ["show"] = true,
                    ["formatter"] = "YTD {@[0]}%",
                    ["position"] = "top",
                    ["fontWeight"] = "bold",
                    ["fontSize"] = 11,
                    ["color"] = Navy,
                },
                ["z"] = 10,
            },
        };

        return await renderer.RenderBase64Async(option, ChartWidth, ChartHeight);
    }

    private static async Task<string?> ChartYieldsAsync(EChartsRenderer renderer, Dictionary<string, double> yields)
    {
        if (yields.Count == 0) return null;

        var names = yields.Keys.ToArray();
        var values = yields.Values.ToArray();

        var barData = names.Select((n, i) => new Dictionary<string, object>
        {
            ["value"] = values[i],
            ["itemStyle"] = new Dictionary<string, object> { ["color"] = n.Contains("High") ? Gold : Navy },
            ["label"] = new Dictionary<string, object>
            {
                ["show"] = true,
                ["position"] = "right",
                ["formatter"] = $"{values[i]:0.00}%",
                ["fontWeight"] = "bold",
                ["color"] = TextColor,
            },
        }).ToArray();

        var option = BaseOption(new Dictionary<string, object> { ["left"] = 130, ["right"] = 60, ["top"] = 20, ["bottom"] = 50 });
        option["xAxis"] = new Dictionary<string, object>
        {
            ["type"] = "value",
            ["name"] = "Απόδοση (%)",
            ["nameLocation"] = "middle",
            ["nameGap"] = 30,
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
            ["splitLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["yAxis"] = new Dictionary<string, object>
        {
            ["type"] = "category",
            ["data"] = names,
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["series"] = new object[]
        {
            new Dictionary<string, object> { ["type"] = "bar", ["data"] = barData, ["barWidth"] = "60%" },
        };

        return await renderer.RenderBase64Async(option, ChartWidth, ChartHeight);
    }

    private static async Task<string?> ChartForexAsync(EChartsRenderer renderer, Dictionary<string, double> forex)
    {
        if (forex.Count == 0) return null;

        var pairs = forex.Keys.ToArray();
        var values = forex.Values.ToArray();
        var colors = new[] { Navy, Orange, Teal };

        var barData = pairs.Select((p, i) => new Dictionary<string, object>
        {
            ["value"] = values[i],
            ["itemStyle"] = new Dictionary<string, object> { ["color"] = colors[i % colors.Length] },
            ["label"] = new Dictionary<string, object>
            {
                ["show"] = true,
                ["position"] = "top",
                ["formatter"] = values[i] >= 10 ? $"{values[i]:0.0}" : $"{values[i]:0.0000}",
                ["fontWeight"] = "bold",
                ["color"] = TextColor,
            },
        }).ToArray();

        var option = BaseOption(new Dictionary<string, object> { ["left"] = 70, ["right"] = 30, ["top"] = 30, ["bottom"] = 40 });
        option["xAxis"] = new Dictionary<string, object>
        {
            ["type"] = "category",
            ["data"] = pairs,
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["yAxis"] = new Dictionary<string, object>
        {
            ["type"] = "value",
            ["name"] = "Τιμή",
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
            ["splitLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["series"] = new object[]
        {
            new Dictionary<string, object> { ["type"] = "bar", ["data"] = barData, ["barWidth"] = "50%" },
        };

        return await renderer.RenderBase64Async(option, ChartWidth, ChartHeight);
    }

    private static async Task<string?> ChartMacroAsync(EChartsRenderer renderer, Dictionary<string, double> macro)
    {
        if (macro.Count == 0) return null;

        var labels = macro.Keys.ToArray();
        var values = macro.Values.ToArray();

        var barData = labels.Select((label, i) =>
        {
            string color;
            if (label.Contains("CPI") || label.Contains("PPI"))
                color = Red;
            else if (label.Contains("Επιτόκιο") || label.Contains("Rate") || label.Contains("Fed"))
                color = Gold;
            else if (label.Contains("Ανεργ") || label.Contains("Unemploy"))
                color = Navy;
            else
                color = Green;

            return new Dictionary<string, object>
            {
                ["value"] = values[i],
                ["itemStyle"] = new Dictionary<string, object> { ["color"] = color },
                ["label"] = new Dictionary<string, object>
                {
                    ["show"] = true,
                    ["position"] = "right",
                    ["formatter"] = $"{values[i]:0.0}%",
                    ["fontWeight"] = "bold",
                    ["color"] = TextColor,
                },
            };
        }).ToArray();

        var option = BaseOption(new Dictionary<string, object> { ["left"] = 140, ["right"] = 60, ["top"] = 20, ["bottom"] = 50 });
        option["xAxis"] = new Dictionary<string, object>
        {
            ["type"] = "value",
            ["name"] = "Τιμή (%)",
            ["nameLocation"] = "middle",
            ["nameGap"] = 30,
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
            ["splitLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["yAxis"] = new Dictionary<string, object>
        {
            ["type"] = "category",
            ["data"] = labels,
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["series"] = new object[]
        {
            new Dictionary<string, object> { ["type"] = "bar", ["data"] = barData, ["barWidth"] = "60%" },
        };

        return await renderer.RenderBase64Async(option, ChartWidth, ChartHeight);
    }

    private static async Task<string?> ChartCommoditiesAsync(EChartsRenderer renderer, Dictionary<string, double> commodities)
    {
        var items = commodities.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (items.Count == 0) return null;

        var labels = items.Keys.ToArray();
        var values = items.Values.ToArray();
        var colors = new[] { Gold, Navy, Teal };

        var barData = labels.Select((label, i) => new Dictionary<string, object>
        {
            ["value"] = values[i],
            ["itemStyle"] = new Dictionary<string, object> { ["color"] = colors[i % colors.Length] },
            ["label"] = new Dictionary<string, object>
            {
                ["show"] = true,
                ["position"] = "top",
                ["formatter"] = $"{values[i]:0.00}",
                ["fontWeight"] = "bold",
                ["color"] = TextColor,
            },
        }).ToArray();

        var option = BaseOption(new Dictionary<string, object> { ["left"] = 70, ["right"] = 30, ["top"] = 30, ["bottom"] = 40 });
        option["xAxis"] = new Dictionary<string, object>
        {
            ["type"] = "category",
            ["data"] = labels,
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["yAxis"] = new Dictionary<string, object>
        {
            ["type"] = "value",
            ["name"] = "Τιμή (USD)",
            ["axisLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
            ["splitLine"] = new Dictionary<string, object> { ["lineStyle"] = new Dictionary<string, object> { ["color"] = GridColor } },
        };
        option["series"] = new object[]
        {
            new Dictionary<string, object> { ["type"] = "bar", ["data"] = barData, ["barWidth"] = "50%" },
        };

        return await renderer.RenderBase64Async(option, ChartWidth, ChartHeight);
    }

    private static string FormatSigned(double value) => value >= 0 ? $"+{value:0.0}%" : $"{value:0.0}%";
}
