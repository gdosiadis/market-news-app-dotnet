using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MarketNewsApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveProductionConfigurationToSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CopilotModel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AzureEndpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    AzureDeployment = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AzureApiVersion = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Recipients = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    FromDisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SubjectTemplate = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeatureFlags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureFlags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Prompts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Template = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prompts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LookbackDays = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxSummarySourceCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxTranslationSourceCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    IncludeTranslatedContent = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeSourceList = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchedulingSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DailySendTime = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchedulingSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScrapeSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SelectorsJson = table.Column<string>(type: "TEXT", nullable: false),
                    WaitFor = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TimeoutMs = table.Column<int>(type: "INTEGER", nullable: false),
                    ExtraSettleMs = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpandButtonTextsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExcludeSelectorsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ScreenshotSelectorsJson = table.Column<string>(type: "TEXT", nullable: true),
                    FollowFirstLinkSelector = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapeSources", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AgentSettings",
                columns: new[] { "Id", "AzureApiVersion", "AzureDeployment", "AzureEndpoint", "CopilotModel", "Provider" },
                values: new object[] { 1, "2024-10-21", null, null, null, "copilot" });

            migrationBuilder.InsertData(
                table: "EmailSettings",
                columns: new[] { "Id", "FromDisplayName", "Recipients", "SubjectTemplate" },
                values: new object[] { 1, "Market News AI", "recipient1@example.com,recipient2@example.com", "Market News AI — {{date}}" });

            migrationBuilder.InsertData(
                table: "FeatureFlags",
                columns: new[] { "Id", "IsEnabled", "Key" },
                values: new object[,]
                {
                    { 1, true, "scrape-cache" },
                    { 2, true, "summary-cache" }
                });

            migrationBuilder.InsertData(
                table: "Prompts",
                columns: new[] { "Id", "IsEnabled", "Key", "Template" },
                values: new object[,]
                {
                    { 1, true, "source-system", "Είσαι επιμελητής που αποδίδει στα ελληνικά ΜΟΝΟ τις πληροφορίες του παρεχόμενου scraped κειμένου. Η ακρίβεια και η πιστότητα στην πηγή είναι σημαντικότερες από την έκταση ή την ερμηνεία. Χρησιμοποιείς HTML formatting με h3, ul/li, strong και table class=market-table. Χρησιμοποιείς αποκλειστικά το περιεχόμενο της συγκεκριμένης πηγής, δεν προσθέτεις γεγονότα ή επενδυτικές συστάσεις, και διατηρείς αριθμούς, ποσοστά, ημερομηνίες και επιφυλάξεις." },
                    { 2, true, "source-user", "Σήμερα είναι {{today}}. Παρακάτω δίνεται το περιεχόμενο ΑΠΟΚΛΕΙΣΤΙΚΑ από την πηγή «{{sourceName}}» ({{sourceUrl}}) για την περίοδο {{sinceDate}} – {{today}}. Απόδωσε στα ελληνικά μόνο τα γεγονότα, αριθμούς και ρητές θέσεις. Ξεκίνα με <div class=\"section\"><h2>📄 {{sourceName}}</h2><p class=\"source-tag\">Πηγή: <a href=\"{{sourceUrl}}\">{{sourceName}}</a></p> και κλείσε με </div>. Αν το περιεχόμενο είναι μόνο disclaimer, όροι, cookie/privacy notice ή απαιτεί σύνδεση, επέστρεψε μόνο NO_CONTENT. Γράψε μόνο HTML. ΠΕΡΙΕΧΟΜΕΝΟ ΠΗΓΗΣ: {{content}}" },
                    { 3, true, "translation", "Μετέφρασε πιστά στα ελληνικά το παρακάτω scraped περιεχόμενο από την πηγή «{{sourceName}}». Μην το συνοψίσεις, μην προσθέσεις πληροφορίες και μην χρησιμοποιήσεις HTML ή Markdown. Διατήρησε αλλαγές γραμμής, αριθμούς, ονόματα εταιρειών και χρηματοοικονομικούς όρους. ΠΕΡΙΕΧΟΜΕΝΟ: {{content}}" },
                    { 4, true, "synthesis", "Με βάση τις αναλύσεις ανά πηγή για {{sinceDate}} – {{today}}, σύνταξε ΣΥΝΘΕΤΙΚΗ ΕΠΙΣΚΟΠΗΣΗ σε HTML στα ΕΛΛΗΝΙΚΑ. Κάλυψε κοινά θέματα, αποκλίσεις, συνολική αξιολόγηση και επενδυτικές συστάσεις που προκύπτουν από τις πηγές. Ξεκίνα με <div class=\"section synthesis\"><h2>🔍 Συνθετική Επισκόπηση Αγορών — {{today}}</h2> και κλείσε με </div>. Μόνο HTML. ΑΝΑΛΥΣΕΙΣ ΑΝΑ ΠΗΓΗ: {{snippets}}" }
                });

            migrationBuilder.InsertData(
                table: "ReportSettings",
                columns: new[] { "Id", "IncludeSourceList", "IncludeTranslatedContent", "LookbackDays", "MaxSummarySourceCharacters", "MaxTranslationSourceCharacters" },
                values: new object[] { 1, true, true, 10, 20000, 30000 });

            migrationBuilder.InsertData(
                table: "SchedulingSettings",
                columns: new[] { "Id", "DailySendTime", "IsEnabled" },
                values: new object[] { 1, "07:00", true });

            migrationBuilder.InsertData(
                table: "ScrapeSources",
                columns: new[] { "Id", "ExcludeSelectorsJson", "ExpandButtonTextsJson", "ExtraSettleMs", "FollowFirstLinkSelector", "IsEnabled", "Name", "ScreenshotSelectorsJson", "SelectorsJson", "SortOrder", "TimeoutMs", "Url", "WaitFor" },
                values: new object[,]
                {
                    { 1, null, null, 0, null, true, "Bloomberg Markets", null, "[\"article\",\"[data-component='headline']\",\"h1\",\"h2\",\"h3\",\".story-package-module__headline\"]", 1, 20000, "https://www.bloomberg.com/markets", "body" },
                    { 2, null, null, 0, null, true, "BlackRock Investment Institute", null, "[\"article\",\".content-block\",\"h1\",\"h2\",\"p\",\".editorial-content\"]", 2, 25000, "https://www.blackrock.com/corporate/insights/blackrock-investment-institute/publications/weekly-commentary", "body" },
                    { 3, null, null, 0, null, true, "T. Rowe Price Global Markets", null, "[\"article\",\"main\",\".article-body\",\"h1\",\"h2\",\"h3\",\"p\"]", 3, 25000, "https://www.troweprice.com/personal-investing/resources/insights/global-markets-weekly-update.html", "main" },
                    { 4, null, null, 0, null, true, "BNP Paribas AM Viewpoint", null, "[\"article\",\"main\",\"p\",\".article-title\",\"h1\",\"h2\",\"h3\",\".card-title\"]", 4, 20000, "https://viewpoint.bnpparibas-am.com/", "body" },
                    { 5, null, null, 0, null, true, "Edward Jones Weekly Update", null, "[\"article\",\"main\",\".article-body\",\"h1\",\"h2\",\"h3\",\"p\"]", 5, 25000, "https://www.edwardjones.com/us-en/market-news-insights/stock-market-news/stock-market-weekly-update", "main" },
                    { 6, "[\".jp-seo-modal-container\",\".jpm-am-overlay-disclaimer\"]", "[\"Read more\"]", 0, null, true, "JPMorgan Weekly Market Recap", null, "[\"article\",\"main\",\".content\",\"h1\",\"h2\",\"h3\",\"p\"]", 6, 25000, "https://am.jpmorgan.com/us/en/asset-management/institutional/insights/market-insights/market-updates/weekly-market-recap/", "body" },
                    { 7, null, null, 0, "#articles-list .chip h2 a", true, "Citi Market Insights", null, "[\"article\",\"main\",\".content-area\",\"h1\",\"h2\",\"h3\",\"p\"]", 7, 25000, "https://marketinsights.citi.com/Market-Commentary/Weekly-Market-Update/index.html", "body" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_Key",
                table: "FeatureFlags",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_Key",
                table: "Prompts",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrapeSources_Name",
                table: "ScrapeSources",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSettings");

            migrationBuilder.DropTable(
                name: "EmailSettings");

            migrationBuilder.DropTable(
                name: "FeatureFlags");

            migrationBuilder.DropTable(
                name: "Prompts");

            migrationBuilder.DropTable(
                name: "ReportSettings");

            migrationBuilder.DropTable(
                name: "SchedulingSettings");

            migrationBuilder.DropTable(
                name: "ScrapeSources");
        }
    }
}
