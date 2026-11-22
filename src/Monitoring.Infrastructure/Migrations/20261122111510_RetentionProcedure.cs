using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitoring.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetentionProcedure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceState",
                columns: table => new
                {
                    Task = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InitializedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSuccessUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastDurationMs = table.Column<long>(type: "bigint", nullable: true),
                    LastSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceState", x => x.Task);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_Status_EndedUtc",
                table: "JobRuns",
                columns: new[] { "Status", "EndedUtc" });
            // Versioned stored procedure: a behaviour change ships as ..._v2 in a new migration, never as an edit of this one.
            migrationBuilder.Sql(@"
CREATE PROCEDURE dbo.usp_Monitoring_ApplyRetention_v1
    @JobHistoryRetentionDays int = 30,
    @AuditRetentionDays int = 365,
    @ChangeFeedRetentionDays int = 7,
    @BatchSize int = 1000,
    @MaxBatches int = NULL,
    @NowUtc datetime2 = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @JobHistoryRetentionDays IS NULL OR @JobHistoryRetentionDays < 1 OR @JobHistoryRetentionDays > 36500
        THROW 50001, 'JobHistoryRetentionDays must be between 1 and 36500.', 1;
    IF @AuditRetentionDays IS NULL OR @AuditRetentionDays < 1 OR @AuditRetentionDays > 36500
        THROW 50002, 'AuditRetentionDays must be between 1 and 36500.', 1;
    IF @ChangeFeedRetentionDays IS NULL OR @ChangeFeedRetentionDays < 1 OR @ChangeFeedRetentionDays > 36500
        THROW 50003, 'ChangeFeedRetentionDays must be between 1 and 36500.', 1;
    IF @BatchSize IS NULL OR @BatchSize < 1 OR @BatchSize > 10000
        THROW 50004, 'BatchSize must be between 1 and 10000.', 1;
    IF @MaxBatches IS NOT NULL AND @MaxBatches < 1
        THROW 50005, 'MaxBatches must be NULL (no limit) or at least 1.', 1;

    IF @NowUtc IS NULL SET @NowUtc = SYSUTCDATETIME();

    DECLARE @JobCutoff datetime2 = DATEADD(DAY, -@JobHistoryRetentionDays, @NowUtc);
    DECLARE @AuditCutoff datetime2 = DATEADD(DAY, -@AuditRetentionDays, @NowUtc);
    DECLARE @FeedCutoff datetime2 = DATEADD(DAY, -@ChangeFeedRetentionDays, @NowUtc);

    DECLARE @runsDeleted int = 0, @attemptsDeleted int = 0, @auditDeleted int = 0, @feedDeleted int = 0;
    DECLARE @n int, @a int, @batches int;
    DECLARE @ids TABLE (Id uniqueidentifier NOT NULL PRIMARY KEY);

    -- 1) Job history: terminal runs only, by the time the run ended. Attempts are removed with their run in the same
    --    short transaction (attempts first: the foreign key cascades, but explicit deletes keep the counts and the plan simple).
    SET @batches = 0;
    WHILE (@MaxBatches IS NULL OR @batches < @MaxBatches)
    BEGIN
        BEGIN TRANSACTION;
        DELETE FROM @ids;
        INSERT INTO @ids (Id)
        SELECT TOP (@BatchSize) Id
        FROM dbo.JobRuns WITH (UPDLOCK, ROWLOCK, READPAST)
        WHERE Status IN ('Succeeded', 'Failed', 'Cancelled', 'TimedOut', 'Abandoned')
          AND EndedUtc IS NOT NULL AND EndedUtc < @JobCutoff
        ORDER BY EndedUtc;
        SET @n = @@ROWCOUNT;
        IF @n > 0
        BEGIN
            DELETE a FROM dbo.RunAttempts a INNER JOIN @ids i ON a.RunId = i.Id;
            SET @a = @@ROWCOUNT;
            DELETE r FROM dbo.JobRuns r INNER JOIN @ids i ON r.Id = i.Id;
            SET @attemptsDeleted += @a;
            SET @runsDeleted += @n;
        END
        COMMIT TRANSACTION;
        SET @batches += 1;
        IF @n = 0 BREAK;
    END

    -- 2) Audit entries: their own policy, by the time the entry was written.
    SET @batches = 0;
    WHILE (@MaxBatches IS NULL OR @batches < @MaxBatches)
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.AuditEntries WHERE Utc < @AuditCutoff;
        SET @n = @@ROWCOUNT;
        SET @auditDeleted += @n;
        SET @batches += 1;
        IF @n = 0 BREAK;
    END

    -- 3) Change feed: only rows that were already dispatched, by dispatch time. Pending rows are never touched.
    SET @batches = 0;
    WHILE (@MaxBatches IS NULL OR @batches < @MaxBatches)
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.ChangeEvents WHERE DispatchedUtc IS NOT NULL AND DispatchedUtc < @FeedCutoff;
        SET @n = @@ROWCOUNT;
        SET @feedDeleted += @n;
        SET @batches += 1;
        IF @n = 0 BREAK;
    END

    SELECT EntityType, DeletedCount, HasMore
    FROM (VALUES
        ('JobRuns', @runsDeleted, CASE WHEN EXISTS (SELECT 1 FROM dbo.JobRuns
            WHERE Status IN ('Succeeded', 'Failed', 'Cancelled', 'TimedOut', 'Abandoned') AND EndedUtc IS NOT NULL AND EndedUtc < @JobCutoff) THEN 1 ELSE 0 END),
        ('RunAttempts', @attemptsDeleted, 0),
        ('AuditEntries', @auditDeleted, CASE WHEN EXISTS (SELECT 1 FROM dbo.AuditEntries WHERE Utc < @AuditCutoff) THEN 1 ELSE 0 END),
        ('ChangeEvents', @feedDeleted, CASE WHEN EXISTS (SELECT 1 FROM dbo.ChangeEvents
            WHERE DispatchedUtc IS NOT NULL AND DispatchedUtc < @FeedCutoff) THEN 1 ELSE 0 END)
    ) AS r (EntityType, DeletedCount, HasMore);
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_Monitoring_ApplyRetention_v1");

            migrationBuilder.DropTable(
                name: "MaintenanceState");

            migrationBuilder.DropIndex(
                name: "IX_JobRuns_Status_EndedUtc",
                table: "JobRuns");
        }
    }
}
