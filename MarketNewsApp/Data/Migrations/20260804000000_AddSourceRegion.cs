using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260804000000_AddSourceRegion")]
    public partial class AddSourceRegion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceRegion",
                table: "ScrapeSources",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "International");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceRegion",
                table: "ScrapeSources");
        }
    }
}