using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260804000100_ConfigureGreekArticleLinks")]
    public partial class ConfigureGreekArticleLinks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET FollowFirstLinkSelector = CASE Name
                    WHEN 'Capital' THEN 'a[href*="/agores/"]'
                    WHEN 'Euro2Day' THEN 'a[href*="/news/"][href*="/article/"]'
                    WHEN 'Insider' THEN 'a[href^="/agores/"][href*="/"]'
                    WHEN 'Newmoney' THEN 'a[href*="/roh/agores/"]'
                END
                WHERE Name IN ('Capital', 'Euro2Day', 'Insider', 'Newmoney');
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET FollowFirstLinkSelector = NULL
                WHERE Name IN ('Capital', 'Euro2Day', 'Insider', 'Newmoney');
                """);
        }
    }
}