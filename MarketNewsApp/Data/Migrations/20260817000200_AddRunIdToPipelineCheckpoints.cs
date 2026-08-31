using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketNewsApp.Data.Migrations
{
    [DbContext(typeof(MarketNewsDbContext))]
    [Migration("20260817000200_AddRunIdToPipelineCheckpoints")]
    public partial class AddRunIdToPipelineCheckpoints : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineCheckpointsNew",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RunDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_PipelineCheckpointsNew", item => new { item.RunId, item.Stage, item.SourceName }));

            migrationBuilder.Sql("INSERT INTO PipelineCheckpointsNew (RunId, RunDate, Stage, SourceName, ContentHash, PayloadJson, UpdatedAt) SELECT CASE WHEN RunDate = '2026-08-17' THEN 'f30b713de2304030bce1564b232f155f' ELSE 'legacy-' || RunDate END, RunDate, Stage, SourceName, ContentHash, PayloadJson, UpdatedAt FROM PipelineCheckpoints;");
            migrationBuilder.DropTable(name: "PipelineCheckpoints");
            migrationBuilder.RenameTable(name: "PipelineCheckpointsNew", newName: "PipelineCheckpoints");
            migrationBuilder.CreateIndex(name: "IX_PipelineCheckpoints_RunDate_Stage", table: "PipelineCheckpoints", columns: new[] { "RunDate", "Stage" });
        }

        protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Reverting run-scoped checkpoints would discard run history.");
    }
}