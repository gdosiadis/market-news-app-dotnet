using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260804000400_ExcludeCapitalConsentOverlay")]
    public partial class ExcludeCapitalConsentOverlay : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET ExcludeSelectorsJson = '["[id^=\"sp_message_container\"]","#onetrust-consent-sdk",".qc-cmp2-container"]'
                WHERE Name = 'Capital';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET ExcludeSelectorsJson = NULL
                WHERE Name = 'Capital';
                """);
        }
    }
}