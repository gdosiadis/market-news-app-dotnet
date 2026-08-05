using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260804000300_UseCapitalSharedArticleBody")]
    public partial class UseCapitalSharedArticleBody : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET SelectorsJson = '["[class^=\"article__body__\"]"]'
                WHERE Name = 'Capital';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET SelectorsJson = '[".article__body__petrelaio"]'
                WHERE Name = 'Capital';
                """);
        }
    }
}