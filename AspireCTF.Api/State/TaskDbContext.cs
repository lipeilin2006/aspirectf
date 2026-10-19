using AspireCTF.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AspireCTF.Api.State;

public sealed class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<ChallengeTask> Tasks => Set<ChallengeTask>();
    public DbSet<PlatformSettings> Settings => Set<PlatformSettings>();
    public DbSet<TaskLog> TaskLogs => Set<TaskLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<ChallengeTask>();
        task.HasKey(x => x.Id);
        task.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        task.Property(x => x.Name).HasMaxLength(256).IsRequired();
        task.Property(x => x.CreatedBy).HasMaxLength(64).IsRequired();
        task.Property(x => x.Category).HasMaxLength(64);
        task.Property(x => x.Model).HasMaxLength(256);
        task.Property(x => x.ContainerName).HasMaxLength(128);
        task.Property(x => x.Executor).HasMaxLength(64);
        task.Property(x => x.FailureReason);
        task.Property(x => x.Writeup).HasColumnType("text");
        task.HasIndex(x => x.CreatedAt);
        task.HasIndex(x => x.Status);

        var log = modelBuilder.Entity<TaskLog>();
        log.HasKey(x => x.Id);
        log.HasIndex(x => new { x.TaskId, x.Seq });
        log.Property(x => x.Stream).HasMaxLength(32);
        log.Property(x => x.Part);

        var settings = modelBuilder.Entity<PlatformSettings>();
        settings.HasKey(x => x.Id);
        settings.Property(x => x.SandboxImage).HasMaxLength(256);
        settings.Property(x => x.ApiKeysProtectedJson).HasColumnType("text");
    }
}
