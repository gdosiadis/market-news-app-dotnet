using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260804000600_RestrictEuro2DayToMarketArticles")]
    public partial class RestrictEuro2DayToMarketArticles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET FollowFirstLinkSelector = 'a[href*="/news/market/article/"]'
                WHERE Name = 'Euro2Day';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET FollowFirstLinkSelector = 'a[href*="/news/"][href*="/article/"]'
                WHERE Name = 'Euro2Day';
                """);
        }
    }
}