using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260814000200_ReadAllNewmoneyGreekMarketArticles")]
    public partial class ReadAllNewmoneyGreekMarketArticles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET FollowFirstLinkSelector = 'a[href*="/roh/agores/"]'
                WHERE Name = 'Newmoney';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET FollowFirstLinkSelector = 'a[href*="/roh/agores/chrimatistirio"]'
                WHERE Name = 'Newmoney';
                """);
        }
    }
}