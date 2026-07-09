using MarketNewsApp.Models;
using ScottPlot;

namespace MarketNewsApp.Services;

public class ChartGenerator
{
    // Light theme matching the Optima Bank brand palette (white background, navy/orange/gold bars) —
    // mirrors the reference "Weekly Supportive material" deck's chart style, not a generic dark theme.
    private static readonly ScottPlot.Color White = ScottPlot.Color.FromHex("#FFFFFF");
    private static readonly ScottPlot.Color Navy = ScottPlot.Color.FromHex("#1B1B3A");
    private static readonly ScottPlot.Color Orange = ScottPlot.Color.FromHex("#FF8B00");
    private static readonly ScottPlot.Color Gold = ScottPlot.Color.FromHex("#E8B84B");
    private static readonly ScottPlot.Color Teal = ScottPlot.Color.FromHex("#2E7D8C");
    private static readonly ScottPlot.Color Red = ScottPlot.Color.FromHex("#C0392B");
    private static readonly ScottPlot.Color Green = ScottPlot.Color.FromHex("#2E8B57");
    private static readonly ScottPlot.Color TextColor = ScottPlot.Color.FromHex("#1A1A1A");
    private static readonly ScottPlot.Color GridColor = ScottPlot.Color.FromHex("#D9D9D9");

    public Dictionary<string, string> GenerateAll(MarketData data)
    {
        var charts = new Dictionary<string, string>();

        SafeGenerate(charts, "indices", () => ChartIndices(data.Indices));
        SafeGenerate(charts, "yields", () => ChartYields(data.Yields));
        SafeGenerate(charts, "forex", () => ChartForex(data.Forex));
        SafeGenerate(charts, "macro", () => ChartMacro(data.Macro));
        SafeGenerate(charts, "commodities", () => ChartCommodities(data.Commodities));

        return charts;
    }

    private static void SafeGenerate(Dictionary<string, string> charts, string name, Func<string?> generator)
    {
        try
        {
            var result = generator();
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

    private static void ApplyLightTheme(Plot plot)
    {
        plot.FigureBackground.Color = White;
        plot.DataBackground.Color = White;
        plot.Axes.Bottom.FrameLineStyle.Color = GridColor;
        plot.Axes.Left.FrameLineStyle.Color = GridColor;
        plot.Axes.Bottom.TickLabelStyle.ForeColor = TextColor;
        plot.Axes.Left.TickLabelStyle.ForeColor = TextColor;
        plot.Axes.Bottom.Label.ForeColor = TextColor;
        plot.Axes.Left.Label.ForeColor = TextColor;
        plot.Grid.MajorLineColor = GridColor;
        plot.Title(string.Empty); // titles are handled by the slide's own header/caption instead
    }

    private string? ChartIndices(Dictionary<string, IndexData> indices)
    {
        if (indices.Count == 0) return null;

        var plt = new Plot();
        ApplyLightTheme(plt);

        var names = indices.Keys.ToArray();
        var weekly = indices.Values.Select(v => v.WeeklyPct).ToArray();
        var ytd = indices.Values.Select(v => v.YtdPct).ToArray();

        var positions = Enumerable.Range(0, names.Length).Select(i => (double)i).ToArray();

        var weeklyBars = new List<ScottPlot.Bar>();
        var ytdBars = new List<ScottPlot.Bar>();

        for (int i = 0; i < names.Length; i++)
        {
            weeklyBars.Add(new ScottPlot.Bar
            {
                Position = positions[i] - 0.2,
                Value = weekly[i],
                Size = 0.35,
                FillColor = Navy,
            });
            ytdBars.Add(new ScottPlot.Bar
            {
                Position = positions[i] + 0.2,
                Value = ytd[i],
                Size = 0.35,
                FillColor = Orange,
            });
        }

        var allBars = weeklyBars.Concat(ytdBars).ToList();
        plt.Add.Bars(allBars.ToArray());

        plt.Axes.Bottom.SetTicks(positions, names);
        plt.Axes.Bottom.TickLabelStyle.Rotation = -15;
        plt.Axes.Left.Label.Text = "Απόδοση (%)";

        return PlotToBase64(plt, 900, 560);
    }

    private string? ChartYields(Dictionary<string, double> yields)
    {
        if (yields.Count == 0) return null;

        var plt = new Plot();
        ApplyLightTheme(plt);

        var names = yields.Keys.ToArray();
        var values = yields.Values.ToArray();
        var positions = Enumerable.Range(0, names.Length).Select(i => (double)i).ToArray();

        var bars = new List<ScottPlot.Bar>();
        for (int i = 0; i < names.Length; i++)
        {
            bars.Add(new ScottPlot.Bar
            {
                Position = positions[i],
                Value = values[i],
                FillColor = names[i].Contains("High") ? Gold : Navy,
                IsVisible = true,
            });
        }

        var barPlot = plt.Add.Bars(bars.ToArray());
        barPlot.Horizontal = true;

        plt.Axes.Left.SetTicks(positions, names);
        plt.Axes.Bottom.Label.Text = "Απόδοση (%)";

        return PlotToBase64(plt, 900, 560);
    }

    private string? ChartForex(Dictionary<string, double> forex)
    {
        if (forex.Count == 0) return null;

        var plt = new Plot();
        ApplyLightTheme(plt);

        var pairs = forex.Keys.ToArray();
        var values = forex.Values.ToArray();
        var positions = Enumerable.Range(0, pairs.Length).Select(i => (double)i).ToArray();
        var colors = new[] { Navy, Orange, Teal };

        var bars = new List<ScottPlot.Bar>();
        for (int i = 0; i < pairs.Length; i++)
        {
            bars.Add(new ScottPlot.Bar
            {
                Position = positions[i],
                Value = values[i],
                Size = 0.5,
                FillColor = colors[i % colors.Length],
            });
        }

        plt.Add.Bars(bars.ToArray());
        plt.Axes.Bottom.SetTicks(positions, pairs);
        plt.Axes.Left.Label.Text = "Τιμή";

        return PlotToBase64(plt, 900, 560);
    }

    private string? ChartMacro(Dictionary<string, double> macro)
    {
        if (macro.Count == 0) return null;

        var plt = new Plot();
        ApplyLightTheme(plt);

        var labels = macro.Keys.ToArray();
        var values = macro.Values.ToArray();
        var positions = Enumerable.Range(0, labels.Length).Select(i => (double)i).ToArray();

        var bars = new List<ScottPlot.Bar>();
        for (int i = 0; i < labels.Length; i++)
        {
            ScottPlot.Color color;
            if (labels[i].Contains("CPI") || labels[i].Contains("PPI"))
                color = Red;
            else if (labels[i].Contains("Επιτόκιο") || labels[i].Contains("Rate") || labels[i].Contains("Fed"))
                color = Gold;
            else if (labels[i].Contains("Ανεργ") || labels[i].Contains("Unemploy"))
                color = Navy;
            else
                color = Green;

            bars.Add(new ScottPlot.Bar
            {
                Position = positions[i],
                Value = values[i],
                FillColor = color,
            });
        }

        var barPlot = plt.Add.Bars(bars.ToArray());
        barPlot.Horizontal = true;

        plt.Axes.Left.SetTicks(positions, labels);
        plt.Axes.Bottom.Label.Text = "Τιμή (%)";

        return PlotToBase64(plt, 900, 560);
    }

    private string? ChartCommodities(Dictionary<string, double> commodities)
    {
        var items = commodities.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (items.Count == 0) return null;

        var plt = new Plot();
        ApplyLightTheme(plt);

        var labels = items.Keys.ToArray();
        var values = items.Values.ToArray();
        var positions = Enumerable.Range(0, labels.Length).Select(i => (double)i).ToArray();
        var colors = new[] { Gold, Navy, Teal };

        var bars = new List<ScottPlot.Bar>();
        for (int i = 0; i < labels.Length; i++)
        {
            bars.Add(new ScottPlot.Bar
            {
                Position = positions[i],
                Value = values[i],
                Size = 0.5,
                FillColor = colors[i % colors.Length],
            });
        }

        plt.Add.Bars(bars.ToArray());
        plt.Axes.Bottom.SetTicks(positions, labels);
        plt.Axes.Left.Label.Text = "Τιμή (USD)";

        return PlotToBase64(plt, 900, 560);
    }

    private static string PlotToBase64(Plot plt, int width, int height)
    {
        var bytes = plt.GetImageBytes(width, height, ImageFormat.Png);
        return Convert.ToBase64String(bytes);
    }
}
