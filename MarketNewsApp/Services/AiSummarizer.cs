using System.Text.RegularExpressions;
using MarketNewsApp.Agents;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

public class AiSummarizer : IAsyncDisposable
{
    private const string SummaryPromptVersion = "source-only-v2";

    // Provider selection/implementation now lives under Agents/ (one file per
    // provider: CopilotChatAgent, GroqChatAgent, AzureOpenAiChatAgent) behind
    // the shared IChatAgent contract — AiSummarizer just talks to whichever
    // one ChatAgentFactory picks. Settings come from an IAgentSettingsProvider
    // (env vars by default) so a future DB-backed provider can be swapped in
    // without changing this class or the agents themselves.
    private readonly IChatAgent _agent;
    private readonly RuntimeConfiguration _configuration;

    private AiSummarizer(IChatAgent agent, RuntimeConfiguration configuration)
    {
        _agent = agent;
        _configuration = configuration;
    }

    public static async Task<AiSummarizer> CreateAsync(RuntimeConfiguration configuration, IAgentSettingsProvider? settingsProvider = null)
    {
        var agent = await ChatAgentFactory.CreateAsync(settingsProvider ?? new SqlAgentSettingsProvider(configuration));
        return new AiSummarizer(agent, configuration);
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
            result[name] = new ScrapedSite { Url = site.Url, SourceRegion = site.SourceRegion, Text = cleaned, Diagnostics = site.Diagnostics, Screenshots = site.Screenshots, PublishedDate = site.PublishedDate };
            Console.WriteLine($"  🧹  {name}: {site.Text.Length:N0} → {cleaned.Length:N0} chars");
        }
        return result;
    }

    // ── Step 4: Per-source summaries (parallel) ───────────────────────────────
    // `previousCache` (optional) holds the per-source result of a previous same-day run,
    // keyed by source name with a content hash. Sources whose cleaned text hash matches
    // the cached one are reused verbatim — only genuinely new/changed content triggers
    // an AI call.
    public async Task<Dictionary<string, SourceSummary>> SummarizePerSourceAsync(
        Dictionary<string, ScrapedSite> sites,
        Dictionary<string, SummaryCache.SourceEntry>? previousCache = null)
    {
        Console.WriteLine($"  🤖  Using {ProviderName}...");
        var today = DateTime.Now.ToString("dd/MM/yyyy");
        var sinceDate = DateTime.Now.AddDays(-_configuration.Report.LookbackDays).ToString("dd/MM/yyyy");
        var systemPrompt = Prompt("source-system");

        var siteList = sites.ToList();
        var sections = new string[siteList.Count];
        var statuses = new SourceStatus[siteList.Count];
        var translations = new string[siteList.Count];

        // Parallel AI calls — max 3 concurrent to avoid rate limiting
        using var aiSemaphore = new SemaphoreSlim(3);
        using var translationSemaphore = new SemaphoreSlim(3);
        var siteTasks = siteList.Select(async (kv, idx) =>
        {
            var (name, info) = (kv.Key, kv.Value);
            var contentHash = SummaryCache.ComputeHash($"{SummaryPromptVersion}\n{info.Text}");

            if (previousCache != null &&
                previousCache.TryGetValue(name, out var cachedEntry) &&
                cachedEntry.ContentHash == contentHash)
            {
                statuses[idx] = cachedEntry.Status;
                sections[idx] = cachedEntry.Html;
                if (cachedEntry.TranslatedContent is not null)
                {
                    translations[idx] = cachedEntry.TranslatedContent;
                }
                else
                {
                    await translationSemaphore.WaitAsync();
                    try
                    {
                        translations[idx] = await TranslateScrapedContentAsync(info.Text, name);
                    }
                    finally
                    {
                        translationSemaphore.Release();
                    }
                }
                Console.WriteLine($"     ♻️  {name} — reused cached summary (unchanged content)");
                return;
            }

            if (string.IsNullOrWhiteSpace(info.Text) || info.Text.StartsWith("["))
            {
                statuses[idx] = SourceStatus.Blocked;
                translations[idx] = "Δεν ανακτήθηκε περιεχόμενο από αυτή την πηγή σήμερα.";
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
            var textContent = info.Text.Length > _configuration.Report.MaxSummarySourceCharacters ? info.Text[.._configuration.Report.MaxSummarySourceCharacters] : info.Text;
            var userPrompt = FormatPrompt("source-user", ("today", today), ("sinceDate", sinceDate), ("sourceName", name), ("sourceUrl", info.Url), ("content", textContent));

            await aiSemaphore.WaitAsync();
            try
            {
                var html = await ChatAsync(
                    [new("system", systemPrompt), new("user", userPrompt)],
                    maxTokens: 3500, temperature: 0.1);
                html = StripLeadingPreamble(StripCodeFences(html));

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
                translations[idx] = await TranslateScrapedContentAsync(info.Text, name);
                Console.WriteLine($"     {StatusIcon(statuses[idx])}  {name} — {statuses[idx]}");
            }
            catch (Exception ex)
            {
                statuses[idx] = SourceStatus.Error;
                translations[idx] = "Η μετάφραση του scraped περιεχομένου δεν ήταν διαθέσιμη αυτή τη στιγμή.";
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
            result[siteList[i].Key] = new SourceSummary(
                sections[i] ?? "",
                statuses[i],
                siteList[i].Value.Url,
                siteList[i].Value.SourceRegion,
                siteList[i].Value.Screenshots,
                translations[i] ?? "Η μετάφραση του scraped περιεχομένου δεν ήταν διαθέσιμη αυτή τη στιγμή.",
                siteList[i].Value.Diagnostics,
                siteList[i].Value.PublishedDate);

        PrintStatusSummary(result);
        return result;
    }

    private async Task<string> TranslateScrapedContentAsync(string scrapedContent, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(scrapedContent) || scrapedContent.StartsWith("["))
            return "Δεν ανακτήθηκε περιεχόμενο από αυτή την πηγή σήμερα.";

        try
        {
            var content = scrapedContent.Length > _configuration.Report.MaxTranslationSourceCharacters ? scrapedContent[.._configuration.Report.MaxTranslationSourceCharacters] : scrapedContent;
            var translation = await ChatAsync([new("user", FormatPrompt("translation", ("sourceName", sourceName), ("content", content)))], maxTokens: 7500, temperature: 0.1);
            return StripCodeFences(translation).Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"     ⚠️  {sourceName} — translation failed: {ex.Message}");
            return "Η μετάφραση του scraped περιεχομένου δεν ήταν διαθέσιμη αυτή τη στιγμή.";
        }
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

    // The model occasionally breaks character and prepends a conversational meta-comment
    // (e.g. "Confirmed — this is exactly the AI synthesis prompt... I'll produce the content
    // it's asking for directly.") before the actual HTML, even though the prompt explicitly
    // says to start with a specific tag. Since every prompt requires the response to start
    // with "<div ...>", drop any leading text before the first "<" character.
    private static string StripLeadingPreamble(string text)
    {
        var tagStart = text.IndexOf('<');
        return tagStart > 0 ? text[tagStart..].TrimStart() : text;
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
        var sinceDate = DateTime.Now.AddDays(-_configuration.Report.LookbackDays).ToString("dd/MM/yyyy");

        var snippets = string.Join("\n\n", perSource
            .Where(kv => kv.Value.Status is SourceStatus.Success or SourceStatus.Partial)
            .Select(kv =>
            {
                var text = Regex.Replace(kv.Value.Html, "<[^>]+>", " ");
                text = Regex.Replace(text, @"\s{2,}", " ").Trim();
                return $"### {kv.Key}\n{(text.Length > 1200 ? text[..1200] : text)}";
            }));

        var prompt = FormatPrompt("synthesis", ("today", today), ("sinceDate", sinceDate), ("snippets", snippets));

        try
        {
            var result = await ChatAsync([new("user", prompt)], maxTokens: 2000, temperature: 0.4);
            return StripLeadingPreamble(StripCodeFences(result));
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
    public string ComposeHtml(
        Dictionary<string, SourceSummary> perSource,
        string synthesis,
        string srcList)
    {
        var today = DateTime.Now.ToString("dd/MM/yyyy");
        var sinceDate = DateTime.Now.AddDays(-_configuration.Report.LookbackDays).ToString("dd/MM/yyyy");

        var statusRows = string.Join("\n", perSource.Select(kv =>
            $"<tr><td>{kv.Key}</td><td>{MarketLabel(kv.Value.SourceRegion)}</td><td>{StatusBadge(kv.Value.Status)}</td></tr>"));

        var intro = $"""
            <div class="section">
            <h2>🗓️ Επισκόπηση Περιόδου {sinceDate} – {today}</h2>
            <p>Αναλυτική ενημέρωση για <strong>διεθνείς αγορές</strong> και <strong>Ελλάδα</strong>, ανά πηγή και με συνθετική επισκόπηση.</p>
            <h3>📋 Κατάσταση ανά πηγή</h3>
            <table class="market-table status-table">
            <tr><th>Πηγή</th><th>Αγορά</th><th>Status</th></tr>
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
        foreach (var group in perSource.GroupBy(item => IsGreekSource(item.Value.SourceRegion)))
        {
            var heading = group.Key
                ? "<div class=\"market-region-heading greek\"><h2>🇬🇷 Ελλάδα</h2><p>Νέα και αναλύσεις από την ελληνική αγορά.</p></div>"
                : "<div class=\"market-region-heading global\"><h2>🌐 Διεθνείς Αγορές</h2><p>Νέα και αναλύσεις από τις διεθνείς αγορές.</p></div>";
            parts.Add(heading);

            foreach (var (name, summary) in group)
            {
                if (string.IsNullOrEmpty(summary.Html)) continue;
                var section = RenderSourceSection(summary.Html, name, summary.Url, summary.PublishedDate);
                parts.Add(_configuration.Report.IncludeTranslatedContent ? AddInlineTranslatedContent(section, summary.TranslatedContent, summary.ScrapeDiagnostics) : section);
                if (summary.Screenshots.Count > 0)
                    parts.Add(BuildScreenshotBlock(name, summary.Screenshots));
            }
        }
        if (_configuration.Report.IncludeSourceList) parts.Add(footer);
        return string.Join("\n", parts);
    }

    private static bool IsGreekSource(string sourceRegion) =>
        string.Equals(sourceRegion, "Greek", StringComparison.OrdinalIgnoreCase);

    private static string MarketLabel(string sourceRegion) => IsGreekSource(sourceRegion) ? "Ελλάδα" : "Διεθνείς";

    private static string RenderSourceSection(string html, string sourceName, string sourceUrl, DateTimeOffset? publishedDate)
    {
        var encodedName = System.Net.WebUtility.HtmlEncode(sourceName);
        var encodedUrl = System.Net.WebUtility.HtmlEncode(sourceUrl);
        var title = $"<h2 class=\"source-title\"><a href=\"{encodedUrl}\" target=\"_blank\">📄 {encodedName}</a></h2>";
        var dateText = publishedDate is not null
            ? publishedDate.Value.ToString("dd/MM/yyyy")
            : $"άγνωστη (ανακτήθηκε {DateTimeOffset.Now:dd/MM/yyyy})";
        var sourceTag = $"<p class=\"source-tag\">Πηγή: <a href=\"{encodedUrl}\" target=\"_blank\">{encodedUrl}</a></p>" +
            $"<p class=\"source-date\">🗓️ Ημερομηνία δημοσίευσης: {dateText}</p>";

        var headingPattern = new Regex(@"<h2(?:\s[^>]*)?>.*?</h2>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var sourceTagPattern = new Regex(@"<p\s+class=\""source-tag\""[^>]*>.*?</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var section = headingPattern.Replace(html, title, count: 1);
        section = sourceTagPattern.Replace(section, sourceTag, count: 1);
        return section;
    }

    private static string AddInlineTranslatedContent(string html, string translatedContent, string diagnostics)
    {
        const string sourceTagPattern = @"<p\s+class=""source-tag"">.*?</p>";
        var content = System.Net.WebUtility.HtmlEncode(translatedContent);
        var interactions = FormatPageInteractions(diagnostics);
        var replacement = $"""
            $0
            <details class="translated-content" style="margin:0 0 14px;padding:8px 10px;background:#0d1117;border:1px solid #30363d;border-radius:6px;">
            <summary style="color:#3fb950;cursor:pointer;">Προβολή πλήρους μεταφρασμένου περιεχομένου</summary>
            <p style="margin:10px 0 6px;color:#d29922;font-size:13px;"><strong>Ενέργειες στη σελίδα</strong></p>
            {interactions}
            <pre style="margin-top:10px;white-space:pre-wrap;word-break:break-word;font-family:monospace;font-size:12px;color:#c9d1d9;">{content}</pre>
            </details>
            """;
        return Regex.Replace(html, sourceTagPattern, replacement, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static string FormatPageInteractions(string diagnostics)
    {
        var actions = diagnostics.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry switch
            {
                var value when value.StartsWith("followed link ", StringComparison.OrdinalIgnoreCase) =>
                    "Ανοίχθηκε ο σύνδεσμος του άρθρου από τη σελίδα καταλόγου.",
                var value when value.StartsWith("dismissed overlay via '", StringComparison.OrdinalIgnoreCase) =>
                    $"Πατήθηκε το κουμπί «{ExtractQuotedValue(value)}» για κλείσιμο αναδυόμενου παραθύρου/όρων.",
                var value when value.StartsWith("expanded ", StringComparison.OrdinalIgnoreCase) =>
                    $"Πατήθηκε «{value[(value.IndexOf(':') + 1)..].Trim()}» για εμφάνιση επιπλέον περιεχομένου.",
                var value when value.StartsWith("closed ", StringComparison.OrdinalIgnoreCase) && value.Contains("obstructing widget", StringComparison.OrdinalIgnoreCase) =>
                    "Έκλεισαν αναδυόμενα στοιχεία που εμπόδιζαν την ανάγνωση της σελίδας.",
                _ => null,
            })
            .Where(action => action is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (actions.Count == 0)
            return "<p style=\"margin:0;color:#8b949e;font-size:12px;\">Δεν χρειάστηκε να πατηθεί κάποιο στοιχείο στη σελίδα.</p>";

        return $"<ul style=\"margin:0;padding-left:18px;color:#c9d1d9;font-size:12px;\">{string.Join(string.Empty, actions.Select(action => $"<li>{System.Net.WebUtility.HtmlEncode(action)}</li>"))}</ul>";
    }

    private static string ExtractQuotedValue(string text)
    {
        var start = text.IndexOf('\'');
        var end = start < 0 ? -1 : text.IndexOf('\'', start + 1);
        return start >= 0 && end > start ? text[(start + 1)..end] : "επιβεβαίωση";
    }

    // Renders a source's page screenshots (charts/tables captured verbatim from the live
    // site, not AI-rendered) as inline images. The <img> src is a cid: placeholder resolved
    // by EmailSender when it attaches the matching screenshot bytes as a linked resource
    // using the same ScreenshotCid naming — see EmailSender.Send.
    private static string BuildScreenshotBlock(string sourceName, IReadOnlyList<string> screenshots)
    {
        var imgs = string.Join("\n", screenshots.Select((_, i) =>
            $"""<img src="cid:{ScreenshotCid(sourceName, i)}" alt="{sourceName} — γράφημα/πίνακας {i + 1}" style="max-width:100%;border-radius:8px;border:1px solid #21262d;margin-top:10px;">"""));
        return $"""
            <div class="screenshot-block">
            {imgs}
            </div>
            """;
    }

    // Deterministic Content-ID for a source's Nth screenshot — shared between ComposeHtml
    // (which references it as "cid:...") and EmailSender (which attaches the actual PNG
    // bytes as a linked resource under this same ID) so the two stay in sync.
    public static string ScreenshotCid(string sourceName, int index) =>
        $"shot_{Regex.Replace(sourceName, "[^a-zA-Z0-9]", "_")}_{index}";

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

    private static string BuildPrompt(Dictionary<string, ScrapedSite> scraped, int maxChars = 800)
    {
        var sections = scraped.Select(kv =>
        {
            var text = kv.Value.Text.Length > maxChars ? kv.Value.Text[..maxChars] : kv.Value.Text;
            return $"### {kv.Key}\nURL: {kv.Value.Url}\n{text}";
        });
        return string.Join("\n\n", sections);
    }

    private string Prompt(string key) => _configuration.Prompts.TryGetValue(key, out var value)
        ? value
        : throw new InvalidOperationException($"Required prompt '{key}' is missing or disabled in SQLite.");

    private string FormatPrompt(string key, params (string Name, string Value)[] tokens)
    {
        var template = Prompt(key);
        foreach (var (name, value) in tokens)
            template = template.Replace($"{{{{{name}}}}}", value, StringComparison.Ordinal);
        return template;
    }

    private Task<string> ChatAsync(List<ChatMessage> messages, int maxTokens = 4096, double temperature = 0.3) =>
        _agent.ChatAsync(messages, maxTokens, temperature);

    private string ProviderName => _agent.ProviderName;

    public async ValueTask DisposeAsync()
    {
        await _agent.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
