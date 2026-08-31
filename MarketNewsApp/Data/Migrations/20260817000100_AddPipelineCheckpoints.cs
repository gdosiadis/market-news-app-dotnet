using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260817000100_AddPipelineCheckpoints")]
    public partial class AddPipelineCheckpoints : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineCheckpoints",
                columns: table => new
                {
                    RunDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_PipelineCheckpoints", item => new { item.RunDate, item.Stage, item.SourceName }));

            migrationBuilder.CreateIndex(
                name: "IX_PipelineCheckpoints_RunDate_Stage",
                table: "PipelineCheckpoints",
                columns: new[] { "RunDate", "Stage" });
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "PipelineCheckpoints");
    }
}