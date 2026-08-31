using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260814000100_CaptureBnpWordPressCharts")]
    public partial class CaptureBnpWordPressCharts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET ScreenshotSelectorsJson = '["img[class*=''wp-image-'']", "svg", "canvas", "table"]'
                WHERE Name = 'BNP Paribas AM Viewpoint';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ScrapeSources
                SET ScreenshotSelectorsJson = NULL
                WHERE Name = 'BNP Paribas AM Viewpoint';
                """);
        }
    }
}