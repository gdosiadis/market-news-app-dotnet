using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260804000500_ClassifyGreekSources")]
    public partial class ClassifyGreekSources : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET SourceRegion = 'Greek'
                WHERE Name IN ('Capital', 'Euro2Day', 'Insider', 'Newmoney');
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET SourceRegion = 'International'
                WHERE Name IN ('Capital', 'Euro2Day', 'Insider', 'Newmoney');
                """);
        }
    }
}