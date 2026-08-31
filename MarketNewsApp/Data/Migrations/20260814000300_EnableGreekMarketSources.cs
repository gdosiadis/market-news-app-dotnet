using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260814000300_EnableGreekMarketSources")]
    public partial class EnableGreekMarketSources : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET IsEnabled = 1
                WHERE Name IN ('Capital', 'Euro2Day', 'Insider', 'Newmoney');
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET IsEnabled = 0
                WHERE Name IN ('Capital', 'Euro2Day', 'Insider', 'Newmoney');
                """);
        }
    }
}