using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitoring.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WorkerCapabilitiesAndAttemptFencing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAvailable",
                table: "JobDefinitions");

            migrationBuilder.AddColumn<int>(
                name: "CountedAttempts",
                table: "JobRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Before this migration "Attempt" was also the retry counter, so existing runs keep their budget usage.
            migrationBuilder.Sql("UPDATE JobRuns SET CountedAttempts = Attempt");

            migrationBuilder.CreateTable(
                name: "WorkerJobs",
                columns: table => new
                {
                    WorkerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    JobKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerJobs", x => new { x.WorkerId, x.JobKey });
                    table.ForeignKey(
                        name: "FK_WorkerJobs_WorkerHeartbeats_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "WorkerHeartbeats",
                        principalColumn: "WorkerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_JobKey",
                table: "WorkerJobs",
                column: "JobKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerJobs");

            migrationBuilder.DropColumn(
                name: "CountedAttempts",
                table: "JobRuns");

            migrationBuilder.AddColumn<bool>(
                name: "IsAvailable",
                table: "JobDefinitions",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }
    }
}
