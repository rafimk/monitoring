using Microsoft.EntityFrameworkCore;
using Monitoring.Contracts;

namespace Monitoring.Infrastructure.Persistence;

public class MonitoringDbContext(DbContextOptions<MonitoringDbContext> options) : DbContext(options)
{
    public DbSet<JobDefinition> Definitions => Set<JobDefinition>();
    public DbSet<JobSchedule> Schedules => Set<JobSchedule>();
    public DbSet<JobRun> Runs => Set<JobRun>();
    public DbSet<RunAttempt> Attempts => Set<RunAttempt>();
    public DbSet<WorkerHeartbeat> Workers => Set<WorkerHeartbeat>();
    public DbSet<WorkerJob> WorkerJobs => Set<WorkerJob>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<ChangeEvent> ChangeEvents => Set<ChangeEvent>();
    public DbSet<MaintenanceState> Maintenance => Set<MaintenanceState>();

    /// <summary>All timestamps are stored as UTC; mark them so they serialize with a 'Z' suffix on the way out.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder cb)
    {
        cb.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        cb.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<JobDefinition>(e =>
        {
            e.ToTable("JobDefinitions");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.TimeZoneId).HasMaxLength(100);
            e.Property(x => x.ConcurrencyPolicy).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.MissedRunPolicy).HasConversion<string>().HasMaxLength(30);
        });

        b.Entity<JobSchedule>(e =>
        {
            e.ToTable("JobSchedules");
            e.HasKey(x => x.Id);
            e.Property(x => x.JobKey).HasMaxLength(100);
            e.HasIndex(x => new { x.Enabled, x.NextDueUtc });
            e.HasIndex(x => x.JobKey).IsUnique(); // one interval schedule per job in v1
            e.HasOne<JobDefinition>().WithMany().HasForeignKey(x => x.JobKey).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JobRun>(e =>
        {
            e.ToTable("JobRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.JobKey).HasMaxLength(100);
            e.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.WorkerId).HasMaxLength(100);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.RequestedBy).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.Status, x.NotBeforeUtc });
            e.HasIndex(x => new { x.JobKey, x.QueuedUtc });
            e.HasIndex(x => x.StartedUtc);
            e.HasIndex(x => x.LeaseExpiresUtc);
            e.HasIndex(x => new { x.Status, x.EndedUtc }); // retention: finds old terminal runs without scanning the table
            // Safety net: one scheduled run per (job, occurrence) even if two workers race.
            e.HasIndex(x => new { x.JobKey, x.ScheduledUtc }).IsUnique().HasFilter("[Trigger] = 'Schedule'");
        });

        b.Entity<RunAttempt>(e =>
        {
            e.ToTable("RunAttempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.WorkerId).HasMaxLength(100);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.RunId, x.Attempt }).IsUnique();
            e.HasOne<JobRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WorkerHeartbeat>(e =>
        {
            e.ToTable("WorkerHeartbeats");
            e.HasKey(x => x.WorkerId);
            e.Property(x => x.WorkerId).HasMaxLength(100);
            e.Property(x => x.Host).HasMaxLength(200);
        });

        b.Entity<WorkerJob>(e =>
        {
            e.ToTable("WorkerJobs");
            e.HasKey(x => new { x.WorkerId, x.JobKey });
            e.Property(x => x.WorkerId).HasMaxLength(100);
            e.Property(x => x.JobKey).HasMaxLength(100);
            e.HasIndex(x => x.JobKey);
            e.HasOne<WorkerHeartbeat>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChangeEvent>(e =>
        {
            e.ToTable("ChangeEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.JobKey).HasMaxLength(100);
            e.Property(x => x.Reason).HasMaxLength(40);
            e.HasIndex(x => new { x.DispatchedUtc, x.Id });
            e.HasIndex(x => x.CreatedUtc);
        });

        b.Entity<MaintenanceState>(e =>
        {
            e.ToTable("MaintenanceState");
            e.HasKey(x => x.Task);
            e.Property(x => x.Task).HasMaxLength(50);
            e.Property(x => x.LastSummary).HasMaxLength(1000);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditEntries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.Target).HasMaxLength(200);
            e.Property(x => x.Details).HasMaxLength(2000);
            e.HasIndex(x => x.Utc);
        });
    }
}
