namespace MarketNewsApp.Data;

internal static class ConfigurationSeed
{
    public static readonly ScrapeSourceConfiguration[] Sources =
    [
        Source(1, "Bloomberg Markets", "https://www.bloomberg.com/markets", "[\"article\",\"[data-component='headline']\",\"h1\",\"h2\",\"h3\",\".story-package-module__headline\"]", "body", 20000, 1),
        Source(2, "BlackRock Investment Institute", "https://www.blackrock.com/corporate/insights/blackrock-investment-institute/publications/weekly-commentary", "[\"article\",\".content-block\",\"h1\",\"h2\",\"p\",\".editorial-content\"]", "body", 25000, 2),
        Source(3, "T. Rowe Price Global Markets", "https://www.troweprice.com/personal-investing/resources/insights/global-markets-weekly-update.html", "[\"article\",\"main\",\".article-body\",\"h1\",\"h2\",\"h3\",\"p\"]", "main", 25000, 3),
        Source(4, "BNP Paribas AM Viewpoint", "https://viewpoint.bnpparibas-am.com/", "[\"article\",\"main\",\"p\",\".article-title\",\"h1\",\"h2\",\"h3\",\".card-title\"]", "body", 20000, 4),
        Source(5, "Edward Jones Weekly Update", "https://www.edwardjones.com/us-en/market-news-insights/stock-market-news/stock-market-weekly-update", "[\"article\",\"main\",\".article-body\",\"h1\",\"h2\",\"h3\",\"p\"]", "main", 25000, 5),
        new() { Id = 6, Name = "JPMorgan Weekly Market Recap", Url = "https://am.jpmorgan.com/us/en/asset-management/institutional/insights/market-insights/market-updates/weekly-market-recap/", SelectorsJson = "[\"article\",\"main\",\".content\",\"h1\",\"h2\",\"h3\",\"p\"]", WaitFor = "body", TimeoutMs = 25000, ExpandButtonTextsJson = "[\"Read more\"]", ExcludeSelectorsJson = "[\".jp-seo-modal-container\",\".jpm-am-overlay-disclaimer\"]", IsEnabled = true, SortOrder = 6 },
        new() { Id = 7, Name = "Citi Market Insights", Url = "https://marketinsights.citi.com/Market-Commentary/Weekly-Market-Update/index.html", SelectorsJson = "[\"article\",\"main\",\".content-area\",\"h1\",\"h2\",\"h3\",\"p\"]", WaitFor = "body", TimeoutMs = 25000, FollowFirstLinkSelector = "#articles-list .chip h2 a", IsEnabled = true, SortOrder = 7 },
    ];

    public static readonly PromptConfiguration[] Prompts =
    [
        new() { Id = 1, Key = "source-system", IsEnabled = true, Template = "Είσαι επιμελητής που αποδίδει στα ελληνικά ΜΟΝΟ τις πληροφορίες του παρεχόμενου scraped κειμένου. Η ακρίβεια και η πιστότητα στην πηγή είναι σημαντικότερες από την έκταση ή την ερμηνεία. Χρησιμοποιείς HTML formatting με h3, ul/li, strong και table class=market-table. Χρησιμοποιείς αποκλειστικά το περιεχόμενο της συγκεκριμένης πηγής, δεν προσθέτεις γεγονότα ή επενδυτικές συστάσεις, και διατηρείς αριθμούς, ποσοστά, ημερομηνίες και επιφυλάξεις." },
        new() { Id = 2, Key = "source-user", IsEnabled = true, Template = "Σήμερα είναι {{today}}. Παρακάτω δίνεται το περιεχόμενο ΑΠΟΚΛΕΙΣΤΙΚΑ από την πηγή «{{sourceName}}» ({{sourceUrl}}) για την περίοδο {{sinceDate}} – {{today}}. Απόδωσε στα ελληνικά μόνο τα γεγονότα, αριθμούς και ρητές θέσεις. Ξεκίνα με <div class=\"section\"><h2>📄 {{sourceName}}</h2><p class=\"source-tag\">Πηγή: <a href=\"{{sourceUrl}}\">{{sourceName}}</a></p> και κλείσε με </div>. Αν το περιεχόμενο είναι μόνο disclaimer, όροι, cookie/privacy notice ή απαιτεί σύνδεση, επέστρεψε μόνο NO_CONTENT. Γράψε μόνο HTML. ΠΕΡΙΕΧΟΜΕΝΟ ΠΗΓΗΣ: {{content}}" },
        new() { Id = 3, Key = "translation", IsEnabled = true, Template = "Μετέφρασε πιστά στα ελληνικά το παρακάτω scraped περιεχόμενο από την πηγή «{{sourceName}}». Μην το συνοψίσεις, μην προσθέσεις πληροφορίες και μην χρησιμοποιήσεις HTML ή Markdown. Διατήρησε αλλαγές γραμμής, αριθμούς, ονόματα εταιρειών και χρηματοοικονομικούς όρους. ΠΕΡΙΕΧΟΜΕΝΟ: {{content}}" },
        new() { Id = 4, Key = "synthesis", IsEnabled = true, Template = "Με βάση τις αναλύσεις ανά πηγή για {{sinceDate}} – {{today}}, σύνταξε ΣΥΝΘΕΤΙΚΗ ΕΠΙΣΚΟΠΗΣΗ σε HTML στα ΕΛΛΗΝΙΚΑ. Κάλυψε κοινά θέματα, αποκλίσεις, συνολική αξιολόγηση και επενδυτικές συστάσεις που προκύπτουν από τις πηγές. Ξεκίνα με <div class=\"section synthesis\"><h2>🔍 Συνθετική Επισκόπηση Αγορών — {{today}}</h2> και κλείσε με </div>. Μόνο HTML. ΑΝΑΛΥΣΕΙΣ ΑΝΑ ΠΗΓΗ: {{snippets}}" },
    ];

    public static readonly EmailConfiguration Email = new() { Id = 1, Recipients = "recipient1@example.com,recipient2@example.com", FromDisplayName = "Market News AI", SubjectTemplate = "Market News AI — {{date}}" };
    public static readonly SchedulingConfiguration Schedule = new() { Id = 1, DailySendTime = "07:00", IsEnabled = true };
    public static readonly AgentConfiguration Agent = new() { Id = 1, Provider = "copilot", CopilotModel = null, AzureApiVersion = "2024-10-21" };
    public static readonly ReportConfiguration Report = new() { Id = 1, LookbackDays = 10, MaxSummarySourceCharacters = 20000, MaxTranslationSourceCharacters = 30000, IncludeTranslatedContent = true, IncludeSourceList = true };
    public static readonly FeatureFlag[] Flags = [new() { Id = 1, Key = "scrape-cache", IsEnabled = true }, new() { Id = 2, Key = "summary-cache", IsEnabled = true }];
    public static readonly AdminUser AdminUser = new() { Id = 1, Username = "admin", PasswordHash = "SET_AT_FIRST_LOGIN", Role = "Administrator", IsActive = true, CreatedAt = new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero) };
    public static readonly EmailRecipient[] Recipients = [new() { Id = 1, Address = "recipient1@example.com", DisplayName = "Primary recipient", IsEnabled = true }, new() { Id = 2, Address = "recipient2@example.com", DisplayName = "Secondary recipient", IsEnabled = true }];
    public static readonly ReportTemplateConfiguration ReportTemplate = new() { Id = 1, Name = "Daily market report", SubjectTemplate = "Market News AI - {{date}}", BodyTemplate = "{{ai_summary}}", IsDefault = true, IsEnabled = true };
    public static readonly ApplicationSetting[] ApplicationSettings = [new() { Id = 1, Key = "timezone", Value = "Europe/Athens", Description = "IANA timezone for scheduled reports" }, new() { Id = 2, Key = "configuration-cache-minutes", Value = "5", Description = "Runtime configuration refresh interval" }];

    private static ScrapeSourceConfiguration Source(int id, string name, string url, string selectors, string waitFor, int timeoutMs, int sortOrder) =>
        new() { Id = id, Name = name, Url = url, SelectorsJson = selectors, WaitFor = waitFor, TimeoutMs = timeoutMs, IsEnabled = true, SortOrder = sortOrder };
}