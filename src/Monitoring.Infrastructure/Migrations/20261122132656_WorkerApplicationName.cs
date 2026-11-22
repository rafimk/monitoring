using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitoring.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WorkerApplicationName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplicationName",
                table: "WorkerHeartbeats",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                // Backfill for instances registered before application names existed. Documented in README.
                defaultValue: "Unassigned");

            migrationBuilder.AddColumn<DateTime>(
                name: "StoppedUtc",
                table: "WorkerHeartbeats",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkerName",
                table: "WorkerHeartbeats",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // Old rows: the stable worker name is the instance id without its "-xxxxxxxx" random suffix.
            migrationBuilder.Sql(@"UPDATE dbo.WorkerHeartbeats
SET WorkerName = CASE WHEN LEN(WorkerId) > 9 THEN LEFT(WorkerId, LEN(WorkerId) - 9) ELSE WorkerId END
WHERE WorkerName = N'';");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHeartbeats_ApplicationName_LastSeenUtc",
                table: "WorkerHeartbeats",
                columns: new[] { "ApplicationName", "LastSeenUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkerHeartbeats_ApplicationName_LastSeenUtc",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "ApplicationName",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "StoppedUtc",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "WorkerName",
                table: "WorkerHeartbeats");
        }
    }
}
