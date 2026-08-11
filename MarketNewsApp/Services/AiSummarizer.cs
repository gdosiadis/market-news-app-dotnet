using System.Text.RegularExpressions;
using MarketNewsApp.Agents;
using MarketNewsApp.Models;

namespace MarketNewsApp.Services;

public class AiSummarizer : IAsyncDisposable
{
    private const string SummaryPromptVersion = "source-only-v4-retry-ai-failures";
    private const int OpenAiTranslationSourceCharacters = 6000;

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
                cachedEntry.ContentHash == contentHash &&
                cachedEntry.Status is SourceStatus.Success or SourceStatus.Partial)
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
                PrintStatusReason(name, statuses[idx], info.Diagnostics, reusedFromCache: true);
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
                PrintStatusReason(name, statuses[idx], info.Diagnostics);
                return;
            }

            // Some sources (e.g. JPMorgan) bury the real analysis well past the first
            // few thousand characters behind nav/chart-accessibility text; a small
            // truncation cut it off entirely before the model ever saw it.
            var sourceContent = IsOpenAiProvider ? RemoveOpenAiBoilerplate(info.Text) : info.Text;
            var textContent = sourceContent.Length > _configuration.Report.MaxSummarySourceCharacters ? sourceContent[.._configuration.Report.MaxSummarySourceCharacters] : sourceContent;
            var promptKey = string.Equals(info.SourceRegion, "Greek", StringComparison.OrdinalIgnoreCase) &&
                _configuration.Prompts.ContainsKey("source-user-greek")
                ? "source-user-greek"
                : "source-user";
            var userPrompt = FormatPrompt(promptKey, ("today", today), ("sinceDate", sinceDate), ("sourceName", name), ("sourceUrl", info.Url), ("content", textContent)) +
                "\n\nIMPORTANT: Always produce an HTML summary from the supplied content. Ignore legal notices, jurisdiction restrictions, cookie text, privacy text, terms and conditions, and risk disclaimers when possible, but do not return a sentinel value or refuse to summarize.";

            await aiSemaphore.WaitAsync();
            try
            {
                var html = await SummarizeWithRetriesAsync(name, systemPrompt, userPrompt);

                if (string.IsNullOrWhiteSpace(html) || IsNoContentSentinel(html))
                {
                    statuses[idx] = SourceStatus.DisclaimerOnly;
                    sections[idx] = FallbackSourceSection(name, info.Url, info.Text);
                    Console.WriteLine($"     ⚠️  {name} — AI would not produce a summary after retries; using extracted source text in the report");
                }
                else if (!HasRenderableHtml(html))
                {
                    statuses[idx] = ClassifyStatus(info.Text, html);
                    sections[idx] = PlainTextSummarySection(name, info.Url, html);
                    Console.WriteLine($"     ℹ️  {name} — rendered plain-text AI summary as HTML");
                }
                else
                {
                    statuses[idx] = ClassifyStatus(info.Text, html);
                    sections[idx] = html;
                }
                translations[idx] = await TranslateScrapedContentAsync(info.Text, name);
                Console.WriteLine($"     {StatusIcon(statuses[idx])}  {name} — {statuses[idx]}");
                PrintStatusReason(name, statuses[idx], info.Diagnostics);
            }
            catch (Exception ex)
            {
                // The AI call failed (timeout, transient error, etc.), but the scrape
                // itself already succeeded — never drop that content from the report.
                statuses[idx] = SourceStatus.Error;
                Console.WriteLine($"     {StatusIcon(statuses[idx])}  {name} — AI summary failed ({ex.Message}); using extracted source text in the report");
                sections[idx] = FallbackSourceSection(name, info.Url, info.Text);
                translations[idx] = await TranslateScrapedContentAsync(info.Text, name);
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
            var sourceContent = IsOpenAiProvider ? RemoveOpenAiBoilerplate(scrapedContent) : scrapedContent;
            var maxCharacters = IsOpenAiProvider
                ? Math.Min(_configuration.Report.MaxTranslationSourceCharacters, OpenAiTranslationSourceCharacters)
                : _configuration.Report.MaxTranslationSourceCharacters;
            var content = sourceContent.Length > maxCharacters ? sourceContent[..maxCharacters] : sourceContent;
            var prompt = FormatPrompt("translation", ("sourceName", sourceName), ("content", content));
            if (IsOpenAiProvider)
            {
                prompt += "\n\nΑπόδωσε μόνο την οικονομική και επενδυτική ανάλυση. Παράλειψε " +
                    "νομικούς όρους, cookie/privacy κείμενα, στοιχεία επικοινωνίας και κάθε " +
                    "μη χρηματοοικονομικό ή ευαίσθητο περιεχόμενο.";
            }
            var translation = await ChatAsync([new("user", prompt)], maxTokens: 7500, temperature: 0.1);
            return StripCodeFences(translation).Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"     ⚠️  {sourceName} — translation failed: {ex.Message}");
            return "Η μετάφραση του scraped περιεχομένου δεν ήταν διαθέσιμη αυτή τη στιγμή.";
        }
    }

    private bool IsOpenAiProvider => string.Equals(_agent.ProviderName, "OpenAI", StringComparison.Ordinal);

    private static string RemoveOpenAiBoilerplate(string scrapedContent)
    {
        var boilerplateMarkers = new[]
        {
            "terms and conditions", "privacy", "cookie", "jurisdiction", "legal or regulatory",
            "not intended for", "should not be relied", "disclaimer", "professional adviser",
            "investment advice", "past performance", "all rights reserved"
        };

        // Azure evaluates the complete input before the prompt can ask it to ignore a
        // disclaimer. Remove common scraped legal boilerplate only on the OpenAI route.
        var financialLines = scrapedContent.Split('\n')
            .Where(line => !boilerplateMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return financialLines.Length == 0 ? scrapedContent : string.Join('\n', financialLines);
    }

    // Content thresholds for status classification
    private const int PartialInputThreshold = 500;   // little source content

    private static void PrintStatusReason(string sourceName, SourceStatus status, string scrapeDiagnostics, bool reusedFromCache = false)
    {
        if (status is not (SourceStatus.DisclaimerOnly or SourceStatus.Blocked or SourceStatus.Error)) return;

        var reason = status switch
        {
            SourceStatus.DisclaimerOnly => reusedFromCache
                ? "cached legacy source classification"
                : "legacy source classification",
            SourceStatus.Blocked => "the scraper did not retrieve usable page content",
            SourceStatus.Error => "the AI processing request failed",
            _ => "unknown",
        };
        Console.WriteLine($"        Reason: {reason}");
        if (!string.IsNullOrWhiteSpace(scrapeDiagnostics))
            Console.WriteLine($"        Scrape diagnostics: {scrapeDiagnostics}");
    }

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

    // The model sometimes skips summarizing on the first attempt (empty reply, the
    // NO_CONTENT sentinel, or a refusal). Retry with an increasingly forceful prompt
    // before falling back to raw scraped text, so a real summary is used whenever the
    // model is actually capable of producing one.
    private async Task<string> SummarizeWithRetriesAsync(string name, string systemPrompt, string userPrompt)
    {
        const int maxAttempts = 3;
        var html = "";
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var prompt = attempt == 1
                ? userPrompt
                : userPrompt + $"\n\nYour previous reply was rejected because it did not contain a real HTML summary. Attempt {attempt}/{maxAttempts}: you MUST summarize the market/investment content that is present, even if brief. Do not reply with NO_CONTENT, an apology, or a refusal.";
            try
            {
                html = StripLeadingPreamble(StripCodeFences(await ChatAsync(
                    [new("system", systemPrompt), new("user", prompt)],
                    maxTokens: 3500, temperature: 0.1)));
            }
            catch when (attempt < maxAttempts)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(html) && !IsNoContentSentinel(html))
                return html;

            Console.WriteLine($"     ⏳  {name} — attempt {attempt}/{maxAttempts} produced no usable summary, retrying...");
        }
        return html;
    }

    // The model occasionally still replies with the literal NO_CONTENT sentinel as
    // plain text despite the "always produce a summary" instruction. Only match a
    // SHORT reply so a genuine long summary that happens to mention the phrase isn't
    // discarded.
    private static bool IsNoContentSentinel(string html)
    {
        var stripped = Regex.Replace(html, "<[^>]+>", " ").Trim();
        return stripped.Length <= 60 && stripped.Contains("no_content", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRenderableHtml(string html)
    {
        return html.Contains("<div", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<p", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<h", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<ul", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<table", StringComparison.OrdinalIgnoreCase);
    }

    private static string PlainTextSummarySection(string name, string url, string summary)
    {
        var paragraphs = summary.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => $"<p>{System.Net.WebUtility.HtmlEncode(line)}</p>");
        return $"""
            <div class="section">
            <h2>📄 {System.Net.WebUtility.HtmlEncode(name)}</h2>
            <p class="source-tag">Πηγή: <a href="{System.Net.WebUtility.HtmlEncode(url)}">{System.Net.WebUtility.HtmlEncode(name)}</a></p>
            {string.Join(Environment.NewLine, paragraphs)}
            </div>
            """;
    }

    private static string FallbackSourceSection(string name, string url, string sourceText)
    {
        var paragraphs = sourceText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => $"<p>{System.Net.WebUtility.HtmlEncode(line)}</p>");
        return $"""
            <div class="section">
            <h2>📄 {System.Net.WebUtility.HtmlEncode(name)}</h2>
            <p class="source-tag">Πηγή: <a href="{System.Net.WebUtility.HtmlEncode(url)}">{System.Net.WebUtility.HtmlEncode(name)}</a></p>
            <p><em>Η αυτόματη σύνοψη δεν ήταν διαθέσιμη. Παρακάτω εμφανίζεται το περιεχόμενο που ανακτήθηκε από την πηγή.</em></p>
            {string.Join(Environment.NewLine, paragraphs)}
            </div>
            """;
    }

    private static SourceStatus ClassifyStatus(string sourceText, string html)
    {
        // A terse, valid summary must not turn a substantive source into a failure.
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
