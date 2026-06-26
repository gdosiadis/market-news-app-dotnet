using System.Text.Json;
using System.Text.Json.Serialization;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

public class AiSummarizer
{
    private static readonly string[] Models =
    [
        "llama-3.3-70b-versatile",
        "llama-3.1-8b-instant",
        "openai/gpt-oss-120b",
        "openai/gpt-oss-20b",
        "meta-llama/llama-4-scout-17b-16e-instruct",
        "qwen/qwen3-32b",
    ];

    private static readonly string[] SkipErrors =
        ["rate_limit", "429", "decommissioned", "deprecated", "context_length", "context window", "too long", "bad_request"];

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public AiSummarizer(string apiKey)
    {
        _apiKey = apiKey;
        _http = new HttpClient { BaseAddress = new Uri("https://api.groq.com/") };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    public (string HtmlSummary, MarketData Data) Run(Dictionary<string, ScrapedSite> scraped)
    {
        Console.WriteLine("  🤖  Generating Greek summary via Groq...");
        var html = SummarizeInGreek(scraped);

        Console.WriteLine("  📊  Extracting market data for charts...");
        var data = ExtractMarketData(scraped);

        return (html, data);
    }

    private string SummarizeInGreek(Dictionary<string, ScrapedSite> scraped)
    {
        var today = DateTime.Now.ToString("dd/MM/yyyy");
        var sinceDate = DateTime.Now.AddDays(-10).ToString("dd/MM/yyyy");

        var srcList = string.Join("\n", scraped.Select(kv =>
            $"<li>📄 <strong>{kv.Key}</strong> — <a href=\"{kv.Value.Url}\">{kv.Value.Url}</a></li>"));

        var systemPrompt = """
            Είσαι Senior Investment Strategist που γράφει θεσμικές αναλύσεις στα ΕΛΛΗΝΙΚΑ.
            Γράφεις σε αναλυτικό, επαγγελματικό ύφος (τύπου research note επενδυτικού οίκου).
            Χρησιμοποιείς HTML formatting:
            - <h3> για υποενότητες
            - <ul>/<li> για bullet points (κάθε <li> πλήρης πρόταση/ανάλυση, όχι λέξη-κλειδί)
            - <strong> για έμφαση σε αριθμούς & συμπεράσματα
            - <table class="market-table"> με <th>/<td> για αριθμητικά δεδομένα & αποδόσεις
            - Emoji για οπτική σήμανση (📈📉⚠️✅🏦🌍💰🤖🛢️🎯)
            ΚΑΝΟΝΕΣ:
            1. Αναλύεις ΑΠΟΚΛΕΙΣΤΙΚΑ το περιεχόμενο της ΣΥΓΚΕΚΡΙΜΕΝΗΣ πηγής που σου δίνεται. ΔΕΝ προσθέτεις, ΔΕΝ ανακατεύεις πληροφορίες από άλλες πηγές ή από δική σου γνώση.
            2. Κάνεις ΣΑΦΕΣ σε όλη την ενότητα από πού προέρχεται κάθε πληροφορία (π.χ. «Σύμφωνα με τη συγκεκριμένη πηγή…», «Η ανάλυση της πηγής σημειώνει…»).
            3. Γράφεις ΕΚΤΕΝΩΣ και σε ΒΑΘΟΣ — ΑΠΑΓΟΡΕΥΕΤΑΙ το περιληπτικό ύφος. Τουλάχιστον 3-5 ουσιαστικές παράγραφοι ανά πηγή.
            4. Κάθε ισχυρισμός με ΣΥΓΚΕΚΡΙΜΕΝΑ στοιχεία: αριθμούς, ποσοστά, ονόματα δεικτών/εταιρειών/κεντρικών τραπεζών, ημερομηνίες, επίπεδα τιμών.
            5. Αναλύεις, δεν παραθέτεις απλά — εξηγείς ΓΙΑΤΙ και ΤΙ ΣΗΜΑΙΝΕΙ για τον επενδυτή.
            """;

        var sections = new List<string>();
        var intro =
            $"""
            <div class="section">
            <h2>🗓️ Επισκόπηση Περιόδου {sinceDate} – {today}</h2>
            <p>Αναλυτική ενημέρωση αγορών <strong>ανά πηγή</strong>. Κάθε ενότητα παρακάτω βασίζεται <strong>αποκλειστικά</strong> στη συγκεκριμένη πηγή που αναφέρεται στον τίτλο της.</p>
            </div>
            """;
        sections.Add(intro);

        foreach (var (name, info) in scraped)
        {
            if (string.IsNullOrWhiteSpace(info.Text) || info.Text.StartsWith("["))
            {
                sections.Add(
                    $"""
                    <div class="section">
                    <h2>📄 {name}</h2>
                    <p class="source-tag">Πηγή: <a href="{info.Url}">{info.Url}</a></p>
                    <p><em>Δεν ανακτήθηκε περιεχόμενο από αυτή την πηγή σήμερα.</em></p>
                    </div>
                    """);
                continue;
            }

            var textContent = info.Text.Length > 4000 ? info.Text[..4000] : info.Text;
            var userPrompt = $"""
                Σήμερα είναι {today}. Παρακάτω δίνεται το περιεχόμενο ΑΠΟΚΛΕΙΣΤΙΚΑ από την πηγή «{name}» ({info.Url}) για την περίοδο {sinceDate} – {today}.

                Σύνταξε μία ΕΚΤΕΝΗ, ΒΑΘΙΑ ΑΝΑΛΥΤΙΚΗ ενότητα στα ΕΛΛΗΝΙΚΑ ΜΟΝΟ για όσα αναφέρει αυτή η πηγή.

                Ξεκίνα ΑΚΡΙΒΩΣ ως εξής:
                <div class="section">
                <h2>📄 {name}</h2>
                <p class="source-tag">Πηγή: <a href="{info.Url}">{name}</a></p>

                Στη συνέχεια ανάλυσε σε βάθος (ΤΟΥΛΑΧΙΣΤΟΝ 3-5 παράγραφοι):
                - 📰 Τα σημαντικότερα γεγονότα / ειδήσεις / θέματα που θίγει η πηγή και γιατί έχουν σημασία
                - 📊 Όλα τα συγκεκριμένα αριθμητικά δεδομένα (δείκτες, αποδόσεις, επιτόκια, μάκρο, εμπορεύματα, νομίσματα) — σε <table class="market-table"> όπου υπάρχουν
                - 🎯 Ποια η στρατηγική οπτική/τοποθέτηση της πηγής (overweight/underweight, κλάδοι, γεωγραφίες) και τι σημαίνει για τον επενδυτή
                Κλείσε την ενότητα με </div>.

                ΣΗΜΑΝΤΙΚΟ:
                - Κάνε ΣΑΦΕΣ ότι ΟΛΕΣ οι πληροφορίες προέρχονται από «{name}» (χρησιμοποίησε φράσεις όπως «Σύμφωνα με την {name}…»).
                - ΜΗΝ προσθέτεις πληροφορίες από άλλες πηγές ή από δική σου γνώση. Αν κάτι δεν αναφέρεται στο κείμενο, μην το επινοείς.

                ΠΕΡΙΕΧΟΜΕΝΟ ΠΗΓΗΣ «{name}»:
                {textContent}

                Γράψε ΜΟΝΟ το HTML content (χωρίς <html>/<head>/<body> tags), εκτενώς και με αριθμούς όπου υπάρχουν.
                """;

            try
            {
                var part = Chat(
                    [new("system", systemPrompt), new("user", userPrompt)],
                    maxTokens: 3500, temperature: 0.3);
                sections.Add(part);
                Console.WriteLine($"     ✍️   Ανάλυση πηγής: {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     ⚠️   Απέτυχε η ανάλυση για {name}: {ex.Message}");
                sections.Add(
                    $"""
                    <div class="section">
                    <h2>📄 {name}</h2>
                    <p class="source-tag">Πηγή: <a href="{info.Url}">{info.Url}</a></p>
                    <p><em>Η αναλυτική επεξεργασία δεν ήταν διαθέσιμη αυτή τη στιγμή.</em></p>
                    </div>
                    """);
            }
        }

        var sourcesBlock =
            $"""

            <div class="section">
            <h2>🔗 Πηγές Άντλησης Δεδομένων</h2>
            <ul class="sources-list">
            {srcList}
            </ul>
            </div>
            """;

        return string.Join("\n", sections) + sourcesBlock;
    }

    private MarketData ExtractMarketData(Dictionary<string, ScrapedSite> scraped)
    {
        var rawContent = BuildPrompt(scraped);
        var dataSection = rawContent.Length > 6000 ? rawContent[..6000] : rawContent;
        var userPrompt = """
            From the financial data below, extract ALL available numeric market data and return ONLY valid JSON (no explanation, no markdown).

            Required JSON structure:
            {
              "indices": {
                "S&P 500": {"weekly_pct": 0.93, "ytd_pct": 9.15},
                "Nasdaq":  {"weekly_pct": 2.43, "ytd_pct": 11.71},
                "Dow Jones": {"weekly_pct": 0.68, "ytd_pct": 7.36},
                "Russell 2000": {"weekly_pct": 3.93, "ytd_pct": 19.22},
                "MSCI EAFE": {"weekly_pct": 0.97, "ytd_pct": 9.28},
                "MSCI EM": {"weekly_pct": 0.0, "ytd_pct": 23.36}
              },
              "yields": {
                "2yr Treasury": 4.15,
                "10yr Treasury": 4.53,
                "30yr Treasury": 5.20,
                "US Aggregate": 4.73,
                "Corporate": 5.19,
                "High Yield": 7.40
              },
              "forex": {
                "EUR/USD": 1.16,
                "GBP/USD": 1.34,
                "USD/JPY": 160.20
              },
              "commodities": {
                "WTI Crude ($/bbl)": 84.0,
                "Gold ($/oz)": 0,
                "Copper": 0
              },
              "macro": {
                "CPI y/y %": 4.2,
                "Core CPI %": 2.9,
                "PPI y/y %": 6.5,
                "Unemployment %": 4.3,
                "Fed Rate Upper %": 3.75
              }
            }

            Fill in any data you find. Keep existing values if data is not found. Return ONLY the JSON object.

            DATA:
            """ + dataSection;

        var raw = Chat([new("user", userPrompt)], maxTokens: 1024, temperature: 0.1);

        // Strip markdown code fences
        raw = raw.TrimStart();
        if (raw.StartsWith("```json")) raw = raw[7..];
        else if (raw.StartsWith("```")) raw = raw[3..];
        if (raw.TrimEnd().EndsWith("```")) raw = raw.TrimEnd()[..^3];
        raw = raw.Trim();

        try
        {
            return ParseMarketData(raw);
        }
        catch
        {
            return GetDefaultMarketData();
        }
    }

    private static MarketData ParseMarketData(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = new MarketData();

        if (root.TryGetProperty("indices", out var indices))
        {
            foreach (var prop in indices.EnumerateObject())
            {
                data.Indices[prop.Name] = new IndexData
                {
                    WeeklyPct = prop.Value.TryGetProperty("weekly_pct", out var w) ? w.GetDouble() : 0,
                    YtdPct = prop.Value.TryGetProperty("ytd_pct", out var y) ? y.GetDouble() : 0,
                };
            }
        }

        if (root.TryGetProperty("yields", out var yields))
            foreach (var prop in yields.EnumerateObject())
                data.Yields[prop.Name] = prop.Value.GetDouble();

        if (root.TryGetProperty("forex", out var forex))
            foreach (var prop in forex.EnumerateObject())
                data.Forex[prop.Name] = prop.Value.GetDouble();

        if (root.TryGetProperty("commodities", out var commod))
            foreach (var prop in commod.EnumerateObject())
                data.Commodities[prop.Name] = prop.Value.GetDouble();

        if (root.TryGetProperty("macro", out var macro))
            foreach (var prop in macro.EnumerateObject())
                data.Macro[prop.Name] = prop.Value.GetDouble();

        return data;
    }

    private static MarketData GetDefaultMarketData() => new()
    {
        Indices = new()
        {
            ["S&P 500"] = new() { WeeklyPct = 0.93, YtdPct = 9.15 },
            ["Nasdaq"] = new() { WeeklyPct = 2.43, YtdPct = 11.71 },
            ["Dow Jones"] = new() { WeeklyPct = 0.68, YtdPct = 7.36 },
            ["Russell 2000"] = new() { WeeklyPct = 3.93, YtdPct = 19.22 },
            ["MSCI EAFE"] = new() { WeeklyPct = 0.97, YtdPct = 9.28 },
            ["MSCI EM"] = new() { WeeklyPct = 0.0, YtdPct = 23.36 },
        },
        Yields = new()
        {
            ["2ετές"] = 4.15, ["10ετές"] = 4.53, ["30ετές"] = 5.20,
            ["Αθρ. US"] = 4.73, ["Εταιρικά"] = 5.19, ["High Yield"] = 7.40,
        },
        Forex = new() { ["EUR/USD"] = 1.16, ["GBP/USD"] = 1.34, ["USD/JPY"] = 160.20 },
        Commodities = new() { ["WTI Crude"] = 84.0 },
        Macro = new()
        {
            ["CPI %"] = 4.2, ["Core CPI %"] = 2.9, ["PPI %"] = 6.5,
            ["Ανεργία %"] = 4.3, ["Επιτόκιο Fed %"] = 3.75,
        },
    };

    private static string BuildPrompt(Dictionary<string, ScrapedSite> scraped, int maxChars = 800)
    {
        var sections = scraped.Select(kv =>
        {
            var text = kv.Value.Text.Length > maxChars ? kv.Value.Text[..maxChars] : kv.Value.Text;
            return $"### {kv.Key}\nURL: {kv.Value.Url}\n{text}";
        });
        return string.Join("\n\n", sections);
    }

    private string Chat(List<ChatMessage> messages, int maxTokens = 4096, double temperature = 0.3)
    {
        foreach (var model in Models)
        {
            try
            {
                var request = new
                {
                    model,
                    messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                    temperature,
                    max_tokens = maxTokens,
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = _http.PostAsync("openai/v1/chat/completions", content).GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().ToLower();
                    if (SkipErrors.Any(e => errorBody.Contains(e)))
                    {
                        Console.WriteLine($"  ⚠️  {model} unavailable ({response.StatusCode}), trying next...");
                        continue;
                    }
                    throw new HttpRequestException($"Groq API error: {response.StatusCode} - {errorBody}");
                }

                var responseJson = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(responseJson);
                return doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";
            }
            catch (HttpRequestException) { throw; }
            catch (Exception ex)
            {
                var err = ex.Message.ToLower();
                if (SkipErrors.Any(e => err.Contains(e)))
                {
                    Console.WriteLine($"  ⚠️  {model} unavailable ({ex.GetType().Name}), trying next...");
                    continue;
                }
                throw;
            }
        }
        throw new InvalidOperationException("All Groq models rate-limited. Try again later or upgrade tier at https://console.groq.com/settings/billing");
    }
}

public record ChatMessage(string Role, string Content);
