using AspireCTF.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AspireCTF.Api.State;

public interface ITaskStore
{
    Task SaveAsync(ChallengeTask task, CancellationToken ct = default);
    Task<ChallengeTask?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ChallengeTask>> ListAsync(CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

public sealed class PostgresTaskStore(
    IDbContextFactory<TaskDbContext> factory,
    ILogger<PostgresTaskStore> logger) : ITaskStore
{
    public bool DatabaseConnected { get; private set; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await BootstrapLegacySchemaAsync(db, ct);
                await db.Database.MigrateAsync(ct);
                DatabaseConnected = true;
                logger.LogInformation("PostgreSQL task store is ready");
                return;
            }
            catch (Exception ex) when (attempt < 30)
            {
                logger.LogWarning("PostgreSQL is not ready yet (attempt {Attempt}/30): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static async Task BootstrapLegacySchemaAsync(TaskDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            BEGIN
                IF to_regclass('"Tasks"') IS NOT NULL
                   AND to_regclass('"__EFMigrationsHistory"') IS NULL THEN
                    ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "AllowContinue" boolean NOT NULL DEFAULT TRUE;
                    ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "FailureReason" text;
                    ALTER TABLE "Tasks" ADD COLUMN IF NOT EXISTS "CreatedBy" character varying(64) NOT NULL DEFAULT 'admin';
                    CREATE TABLE "__EFMigrationsHistory" (
                        "MigrationId" character varying(150) NOT NULL,
                        "ProductVersion" character varying(32) NOT NULL,
                        CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                    );
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20261014180227_InitialCreate', '10.0.0');
                END IF;
            END $$;
            """, ct);
    }

    public async Task SaveAsync(ChallengeTask task, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Tasks.FindAsync([task.Id], ct);
        if (existing is null)
        {
            db.Tasks.Add(task);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(task);
        }

        await db.SaveChangesAsync(ct);
        DatabaseConnected = true;
    }

    public async Task<ChallengeTask?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IReadOnlyList<ChallengeTask>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Tasks.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var task = await db.Tasks.FindAsync([id], ct);
        if (task is null)
        {
            return false;
        }

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
