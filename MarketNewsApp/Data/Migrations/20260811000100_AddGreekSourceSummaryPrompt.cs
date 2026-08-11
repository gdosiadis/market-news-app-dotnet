using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260811000100_AddGreekSourceSummaryPrompt")]
    public partial class AddGreekSourceSummaryPrompt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO Prompts (Id, Key, Template, IsEnabled)
                VALUES (5, 'source-user-greek', 'Σήμερα είναι {{today}}. Παρακάτω δίνεται το περιεχόμενο ΑΠΟΚΛΕΙΣΤΙΚΑ από την ελληνική πηγή «{{sourceName}}» ({{sourceUrl}}) για την περίοδο {{sinceDate}} – {{today}}. Απόδωσε στα ελληνικά μόνο ειδήσεις, αριθμούς και αναλύσεις που αφορούν άμεσα την ελληνική οικονομία, το Χρηματιστήριο Αθηνών ή ελληνικές/εισηγμένες εταιρείες. Παράλειψε αυτοτελείς ειδήσεις άλλων χωρών. Αν ένα διεθνές γεγονός εξηγεί άμεση επίδραση στην Ελλάδα, ανάφερέ το μόνο ως σύντομο πλαίσιο. Αν το σχετικό περιεχόμενο είναι περιορισμένο, παρήγαγε σύντομη σύνοψη και ποτέ άρνηση ή NO_CONTENT. Ξεκίνα με <div class="section"><h2>📄 {{sourceName}}</h2><p class="source-tag">Πηγή: <a href="{{sourceUrl}}">{{sourceName}}</a></p> και κλείσε με </div>. Παράγαγε πάντοτε HTML σύνοψη από το διαθέσιμο περιεχόμενο. Γράψε μόνο HTML. ΠΕΡΙΕΧΟΜΕΝΟ ΠΗΓΗΣ: {{content}}', 1);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM Prompts WHERE Key = 'source-user-greek';");
        }
    }
}