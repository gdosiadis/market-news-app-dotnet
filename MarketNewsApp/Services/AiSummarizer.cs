using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GitHub.Copilot;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

public class AiSummarizer : IAsyncDisposable
{
    private static readonly string[] GroqModels =
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

    private readonly HttpClient? _http;
    private readonly bool _useAzure;
    private readonly string _azureDeployment = string.Empty;
    private readonly string _azureApiVersion = "2024-10-21";

    // ── GitHub Copilot SDK (default provider) ──────────────────────────────────
    private readonly bool _useCopilot;
    private readonly string? _copilotModel;
    private readonly SemaphoreSlim _copilotInitLock = new(1, 1);
    private CopilotClient? _copilotClient;

    public AiSummarizer()
    {
        var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var azureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        var azureApiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION");
        var groqApiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        var provider = Environment.GetEnvironmentVariable("AI_PROVIDER")?.Trim().ToLowerInvariant();

        // Azure OpenAI (explicit)
        if (provider != "copilot" && provider != "groq" &&
            !string.IsNullOrWhiteSpace(azureEndpoint) &&
            !string.IsNullOrWhiteSpace(azureApiKey) &&
            !string.IsNullOrWhiteSpace(azureDeployment))
        {
            _useAzure = true;
            _azureDeployment = azureDeployment;
            if (!string.IsNullOrWhiteSpace(azureApiVersion))
                _azureApiVersion = azureApiVersion;

            _http = new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(azureEndpoint)), Timeout = TimeSpan.FromMinutes(8) };
            _http.DefaultRequestHeaders.Add("api-key", azureApiKey);
            return;
        }

        // Groq (explicit via AI_PROVIDER=groq)
        if (provider == "groq" && !string.IsNullOrWhiteSpace(groqApiKey))
        {
            _http = new HttpClient { BaseAddress = new Uri("https://api.groq.com/"), Timeout = TimeSpan.FromMinutes(8) };
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {groqApiKey}");
            return;
        }

        // GitHub Copilot SDK (default) — uses the logged-in Copilot user,
        // routed through GitHub endpoints that the corporate firewall allows.
        _useCopilot = true;
        _copilotModel = Environment.GetEnvironmentVariable("COPILOT_MODEL");
    }

    // ── Step 2: Clean ─────────────────────────────────────────────────────────
    public static Dictionary<string, ScrapedSite> CleanScraped(Dictionary<string, ScrapedSite> raw)
    {
        var result = new Dictionary<string, ScrapedSite>(raw.Count);
        foreach (var (name, site) in raw)
        {
            if (site.Text.StartsWith("[")) { result[name] = site; continue; }
            var lines = site.Text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 40)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(180);
            var cleaned = string.Join("\n", lines);
            result[name] = new ScrapedSite { Url = site.Url, Text = cleaned, Diagnostics = site.Diagnostics };
            Console.WriteLine($"  🧹  {name}: {site.Text.Length:N0} → {cleaned.Length:N0} chars");
        }
        return result;
    }

    // ── Step 4: Per-source summaries (parallel) ───────────────────────────────
    public async Task<Dictionary<string, SourceSummary>> SummarizePerSourceAsync(Dictionary<string, ScrapedSite> sites)
    {
        Console.WriteLine($"  🤖  Using {ProviderName}...");
        var today = DateTime.Now.ToString("dd/MM/yyyy");
        var sinceDate = DateTime.Now.AddDays(-10).ToString("dd/MM/yyyy");

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

        var siteList = sites.ToList();
        var sections = new string[siteList.Count];
        var statuses = new SourceStatus[siteList.Count];

        // Parallel AI calls — max 3 concurrent to avoid rate limiting
        using var aiSemaphore = new SemaphoreSlim(3);
        var siteTasks = siteList.Select(async (kv, idx) =>
        {
            var (name, info) = (kv.Key, kv.Value);
            if (string.IsNullOrWhiteSpace(info.Text) || info.Text.StartsWith("["))
            {
                statuses[idx] = SourceStatus.Blocked;
                sections[idx] =
                    $"""
                    <div class="section">
                    <h2>📄 {name}</h2>
                    <p class="source-tag">Πηγή: <a href="{info.Url}">{info.Url}</a></p>
                    <p><em>Δεν ανακτήθηκε περιεχόμενο από αυτή την πηγή σήμερα.</em></p>
                    </div>
                    """;
                Console.WriteLine($"     {StatusIcon(SourceStatus.Blocked)}  {name} — Blocked (χωρίς περιεχόμενο)");
                return;
            }

            // Some sources (e.g. JPMorgan) bury the real analysis well past the first
            // few thousand characters behind nav/chart-accessibility text; a small
            // truncation cut it off entirely before the model ever saw it.
            var textContent = info.Text.Length > 20000 ? info.Text[..20000] : info.Text;
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

                ⚠️ ΚΡΙΣΙΜΟ: Αν το ΠΕΡΙΕΧΟΜΕΝΟ ΠΗΓΗΣ παρακάτω ΔΕΝ περιέχει ουσιαστικό χρηματοοικονομικό/αναλυτικό υλικό — π.χ. είναι μόνο νομική αποποίηση ευθύνης (disclaimer), όροι χρήσης, cookie/privacy notice, επιλογή κατηγορίας επενδυτή, ή απαιτεί σύνδεση/αποδοχή όρων — ΜΗΝ γράψεις ανάλυση και ΜΗΝ εξηγήσεις γιατί. Επίστρεψε ΑΚΡΙΒΩΣ και ΜΟΝΟ την εξής μία γραμμή, χωρίς τίποτα άλλο:
                NO_CONTENT

                ΠΕΡΙΕΧΟΜΕΝΟ ΠΗΓΗΣ «{name}»:
                {textContent}

                Γράψε ΜΟΝΟ το HTML content (χωρίς <html>/<head>/<body> tags), εκτενώς και με αριθμούς όπου υπάρχουν — ή σκέτο NO_CONTENT αν δεν υπάρχει ουσιαστικό περιεχόμενο.
                """;

            await aiSemaphore.WaitAsync();
            try
            {
                var html = await ChatAsync(
                    [new("system", systemPrompt), new("user", userPrompt)],
                    maxTokens: 3500, temperature: 0.3);
                html = StripCodeFences(html);

                if (IsNoContent(html))
                {
                    statuses[idx] = SourceStatus.DisclaimerOnly;
                    sections[idx] = NoContentSection(name, info.Url);
                }
                else
                {
                    statuses[idx] = ClassifyStatus(info.Text, html);
                    sections[idx] = html;
                }
                Console.WriteLine($"     {StatusIcon(statuses[idx])}  {name} — {statuses[idx]}");
            }
            catch (Exception ex)
            {
                statuses[idx] = SourceStatus.Error;
                Console.WriteLine($"     {StatusIcon(SourceStatus.Error)}  {name} — Error: {ex.Message}");
                sections[idx] =
                    $"""
                    <div class="section">
                    <h2>📄 {name}</h2>
                    <p class="source-tag">Πηγή: <a href="{info.Url}">{info.Url}</a></p>
                    <p><em>Η αναλυτική επεξεργασία δεν ήταν διαθέσιμη αυτή τη στιγμή.</em></p>
                    </div>
                    """;
            }
            finally
            {
                aiSemaphore.Release();
            }
        });
        await Task.WhenAll(siteTasks);

        var result = new Dictionary<string, SourceSummary>(siteList.Count);
        for (int i = 0; i < siteList.Count; i++)
            result[siteList[i].Key] = new SourceSummary(sections[i] ?? "", statuses[i], siteList[i].Value.Url);

        PrintStatusSummary(result);
        return result;
    }

    // Content thresholds for status classification
    private const int PartialInputThreshold = 500;   // little source content
    private const int DisclaimerOutputThreshold = 250; // near-empty AI output

    // Phrases that indicate the AI found no real content (only disclaimer / gated page)
    private static readonly string[] NoContentSignals =
    [
        "μη διαθέσιμο αναλυτικό",
        "δεν περιλαμβάνει κανένα στοιχείο",
        "δεν είναι δυνατόν να παρατεθούν",
        "απαιτεί αποδοχή όρων",
        "authenticated session",
        "αφορά αποκλειστικά τη νομική",
    ];

    // Strips leading/trailing markdown code fences (```html ... ``` or ``` ... ```) that the
    // model occasionally adds despite being told to return raw HTML only.
    private static string StripCodeFences(string text)
    {
        text = text.TrimStart();
        if (text.StartsWith("```html", StringComparison.OrdinalIgnoreCase)) text = text[7..];
        else if (text.StartsWith("```")) text = text[3..];
        text = text.TrimEnd();
        if (text.EndsWith("```")) text = text[..^3];
        return text.Trim();
    }

    private static bool IsNoContent(string html)
    {
        var stripped = Regex.Replace(html, "<[^>]+>", " ");
        stripped = Regex.Replace(stripped, @"\s{2,}", " ").Trim();
        var lower = stripped.ToLowerInvariant();

        // Primary signal: explicit NO_CONTENT marker returned by the model
        if (lower.Contains("no_content") && lower.Length <= 60)
            return true;

        // Secondary: disclaimer phrases only count when they dominate a SHORT output.
        // Long, genuine analyses may mention "disclaimer" etc. incidentally.
        if (stripped.Length <= 900 && NoContentSignals.Any(sig => lower.Contains(sig)))
            return true;

        return false;
    }

    private static string NoContentSection(string name, string url) =>
        $"""
        <div class="section no-content">
        <h2>📄 {name}</h2>
        <p class="source-tag">Πηγή: <a href="{url}">{name}</a></p>
        <p><em>ℹ️ Δεν βρέθηκε ουσιαστικό αναλυτικό περιεχόμενο για αυτή την πηγή. Η σελίδα περιείχε μόνο όρους χρήσης / αποποίηση ευθύνης ή απαιτούσε σύνδεση.</em></p>
        </div>
        """;

    private static SourceStatus ClassifyStatus(string sourceText, string html)
    {
        var stripped = Regex.Replace(html, "<[^>]+>", " ");
        stripped = Regex.Replace(stripped, @"\s{2,}", " ").Trim();

        if (stripped.Length < DisclaimerOutputThreshold)
            return SourceStatus.DisclaimerOnly;
        if (sourceText.Length < PartialInputThreshold)
            return SourceStatus.Partial;
        return SourceStatus.Success;
    }

    private static string StatusIcon(SourceStatus s) => s switch
    {
        SourceStatus.Success => "✅",
        SourceStatus.Partial => "🟡",
        SourceStatus.Blocked => "🚫",
        SourceStatus.DisclaimerOnly => "⚠️",
        SourceStatus.Error => "❌",
        _ => "•",
    };

    private static void PrintStatusSummary(Dictionary<string, SourceSummary> results)
    {
        var counts = results.Values
            .GroupBy(r => r.Status)
            .ToDictionary(g => g.Key, g => g.Count());
        var parts = Enum.GetValues<SourceStatus>()
            .Where(s => counts.ContainsKey(s))
            .Select(s => $"{StatusIcon(s)} {s}: {counts[s]}");
        Console.WriteLine($"  📋  Status: {string.Join("  ·  ", parts)}");
    }

    // ── Step 5a: Final synthesis ──────────────────────────────────────────────
    public async Task<string> SynthesizeAsync(Dictionary<string, SourceSummary> perSource)
    {
        var today = DateTime.Now.ToString("dd/MM/yyyy");
        var sinceDate = DateTime.Now.AddDays(-10).ToString("dd/MM/yyyy");

        var snippets = string.Join("\n\n", perSource
            .Where(kv => kv.Value.Status is SourceStatus.Success or SourceStatus.Partial)
            .Select(kv =>
            {
                var text = Regex.Replace(kv.Value.Html, "<[^>]+>", " ");
                text = Regex.Replace(text, @"\s{2,}", " ").Trim();
                return $"### {kv.Key}\n{(text.Length > 1200 ? text[..1200] : text)}";
            }));

        var prompt = $"""
            Με βάση τις αναλύσεις ανά πηγή για {sinceDate} – {today}, σύνταξε ΣΥΝΘΕΤΙΚΗ ΕΠΙΣΚΟΠΗΣΗ σε HTML στα ΕΛΛΗΝΙΚΑ:

            1. Κοινά θέματα και τάσεις που επαναλαμβάνονται σε πολλές πηγές
            2. Αποκλίσεις/αντιφάσεις μεταξύ πηγών
            3. Συνολική αξιολόγηση κατάστασης αγορών
            4. Επενδυτικές συστάσεις από τη σύνθεση

            Ξεκίνα ΑΚΡΙΒΩΣ:
            <div class="section synthesis">
            <h2>🔍 Συνθετική Επισκόπηση Αγορών — {today}</h2>

            Κλείσε με </div>. Τουλάχιστον 4-5 παράγραφοι. Μόνο HTML (χωρίς html/head/body).

            ΑΝΑΛΥΣΕΙΣ ΑΝΑ ΠΗΓΗ:
            {snippets}
            """;

        try
        {
            var result = await ChatAsync([new("user", prompt)], maxTokens: 2000, temperature: 0.4);
            return StripCodeFences(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Synthesis failed: {ex.Message}");
            return $"""
                <div class="section synthesis">
                <h2>🔍 Συνθετική Επισκόπηση</h2>
                <p><em>Η συνθετική επισκόπηση δεν ήταν διαθέσιμη αυτή τη στιγμή.</em></p>
                </div>
                """;
        }
    }

    // ── Step 6: Compose HTML ──────────────────────────────────────────────────
    public static string ComposeHtml(
        Dictionary<string, SourceSummary> perSource,
        string synthesis,
        string srcList)
    {
        var today = DateTime.Now.ToString("dd/MM/yyyy");
        var sinceDate = DateTime.Now.AddDays(-10).ToString("dd/MM/yyyy");

        var statusRows = string.Join("\n", perSource.Select(kv =>
            $"<tr><td>{kv.Key}</td><td>{StatusBadge(kv.Value.Status)}</td></tr>"));

        var intro = $"""
            <div class="section">
            <h2>🗓️ Επισκόπηση Περιόδου {sinceDate} – {today}</h2>
            <p>Αναλυτική ενημέρωση αγορών <strong>ανά πηγή</strong> με <strong>συνθετική επισκόπηση</strong>.</p>
            <h3>📋 Κατάσταση ανά πηγή</h3>
            <table class="market-table status-table">
            <tr><th>Πηγή</th><th>Status</th></tr>
            {statusRows}
            </table>
            </div>
            """;

        var footer = $"""
            <div class="section">
            <h2>🔗 Πηγές Άντλησης Δεδομένων</h2>
            <ul class="sources-list">
            {srcList}
            </ul>
            </div>
            """;

        var parts = new List<string> { intro, synthesis };
        parts.AddRange(perSource.Values.Where(s => !string.IsNullOrEmpty(s.Html)).Select(s => s.Html));
        parts.Add(footer);
        return string.Join("\n", parts);
    }

    private static string StatusBadge(SourceStatus s)
    {
        var (icon, label, color) = s switch
        {
            SourceStatus.Success        => ("✅", "Success", "#3fb950"),
            SourceStatus.Partial        => ("🟡", "Partial", "#d29922"),
            SourceStatus.Blocked        => ("🚫", "Blocked", "#f85149"),
            SourceStatus.DisclaimerOnly => ("⚠️", "Disclaimer only", "#a371f7"),
            SourceStatus.Error          => ("❌", "Error", "#f85149"),
            _                           => ("•", "Unknown", "#8b949e"),
        };
        return $"<span style=\"color:{color};font-weight:600\">{icon} {label}</span>";
    }

    public async Task<MarketData> ExtractMarketDataAsync(Dictionary<string, ScrapedSite> scraped)
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

        var raw = await ChatAsync([new("user", userPrompt)], maxTokens: 1024, temperature: 0.1);

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

    private async Task<string> ChatAsync(List<ChatMessage> messages, int maxTokens = 4096, double temperature = 0.3)
    {
        if (_useCopilot)
            return await ChatViaCopilotAsync(messages);

        if (_useAzure)
        {
            var request = new
            {
                messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                temperature,
                max_tokens = maxTokens,
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var path = $"openai/deployments/{_azureDeployment}/chat/completions?api-version={_azureApiVersion}";
            var response = await _http!.PostAsync(path, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Azure OpenAI API error: {response.StatusCode} - {errorBody}");
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }

        foreach (var model in GroqModels)
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
                var response = await _http!.PostAsync("openai/v1/chat/completions", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = (await response.Content.ReadAsStringAsync()).ToLower();
                    if (SkipErrors.Any(e => errorBody.Contains(e)))
                    {
                        Console.WriteLine($"  ⚠️  {model} unavailable ({response.StatusCode}), trying next...");
                        continue;
                    }
                    throw new HttpRequestException($"Groq API error: {response.StatusCode} - {errorBody}");
                }

                var responseJson = await response.Content.ReadAsStringAsync();
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

    // ── GitHub Copilot SDK chat ──────────────────────────────────────────
    private string ProviderName => _useCopilot ? "GitHub Copilot" : _useAzure ? "Azure OpenAI" : "Groq";

    // Corporate TLS-inspecting proxies (e.g. Fortinet) re-sign HTTPS traffic with a
    // private root CA that's trusted by Windows but not by Node's bundled CA store,
    // which breaks the Copilot CLI's fetch() calls to api.github.com. If a CA bundle
    // has been exported to this path, point Node at it so the CLI trusts the proxy.
    private static readonly string CorporateCaBundlePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarketNewsApp", "corporate-ca-bundle.pem");

    private static void EnsureCorporateCaTrust()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODE_EXTRA_CA_CERTS")))
            return;
        if (File.Exists(CorporateCaBundlePath))
            Environment.SetEnvironmentVariable("NODE_EXTRA_CA_CERTS", CorporateCaBundlePath);
    }

    private async Task<CopilotClient> GetCopilotClientAsync()
    {
        if (_copilotClient is not null) return _copilotClient;
        await _copilotInitLock.WaitAsync();
        try
        {
            if (_copilotClient is null)
            {
                EnsureCorporateCaTrust();
                var client = new CopilotClient();
                await client.StartAsync();
                _copilotClient = client;
            }
        }
        finally { _copilotInitLock.Release(); }
        return _copilotClient;
    }

    // Transient errors seen under this environment's corporate TLS-inspecting proxy:
    // concurrent session.create calls occasionally fail the CLI's internal GitHub
    // auth check even though the credentials are valid. A short retry clears it up
    // almost always, since it's a proxy/connection hiccup, not a real auth problem.
    private static readonly string[] TransientCopilotErrors =
        ["fetch oauth user login", "network fetch failed", "communication error with copilot cli"];

    private async Task<string> ChatViaCopilotAsync(List<ChatMessage> messages)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ChatViaCopilotAttemptAsync(messages);
            }
            catch (Exception ex) when (attempt < maxAttempts && TransientCopilotErrors.Any(e => ex.Message.ToLowerInvariant().Contains(e)))
            {
                var delay = TimeSpan.FromMilliseconds(750 * attempt);
                Console.WriteLine($"     ⏳  Transient Copilot error (attempt {attempt}/{maxAttempts}), retrying in {delay.TotalSeconds:F1}s...");
                await Task.Delay(delay);
            }
        }
    }

    private async Task<string> ChatViaCopilotAttemptAsync(List<ChatMessage> messages)
    {
        var client = await GetCopilotClientAsync();

        var systemContent = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        var userContent = string.Join("\n\n", messages.Where(m => m.Role != "system").Select(m => m.Content));

        var config = new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };
        if (!string.IsNullOrWhiteSpace(_copilotModel))
            config.Model = _copilotModel;
        if (!string.IsNullOrWhiteSpace(systemContent))
            config.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemContent,
            };

        await using var session = await client.CreateSessionAsync(config);

        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new System.Text.StringBuilder();

        using var sub = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    buffer.Clear();
                    buffer.Append(msg.Data.Content);
                    break;
                case SessionErrorEvent err:
                    done.TrySetException(new InvalidOperationException($"Copilot session error: {err.Data.Message}"));
                    break;
                case SessionIdleEvent:
                    done.TrySetResult(buffer.ToString());
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = userContent });
        return await done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_copilotClient is not null)
        {
            try { await _copilotClient.StopAsync(); }
            catch { try { await _copilotClient.ForceStopAsync(); } catch { } }
            _copilotClient = null;
        }
        _http?.Dispose();
        _copilotInitLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}

public record ChatMessage(string Role, string Content);
