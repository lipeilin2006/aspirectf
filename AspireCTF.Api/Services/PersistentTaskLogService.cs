using AspireCTF.Api.Models;
using AspireCTF.Api.State;
using Microsoft.EntityFrameworkCore;

namespace AspireCTF.Api.Services;

public sealed class PersistentTaskLogService(IDbContextFactory<TaskDbContext> factory)
{
    public async Task SaveAsync(string taskId, LogLineDto line, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.TaskLogs.Add(new TaskLog
        {
            TaskId = taskId,
            Seq = line.Seq,
            At = DateTimeOffset.Parse(line.At),
            Stream = line.Stream,
            Text = line.Text,
            Part = line.Part
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<LogLineDto>> ListAsync(string taskId, long afterSeq = 0, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TaskLogs.AsNoTracking().Where(x => x.TaskId == taskId && x.Seq > afterSeq)
            .OrderBy(x => x.Seq).Select(x => new LogLineDto(x.Seq, x.At.ToString("O"), x.Stream, x.Text, x.Part)).ToListAsync(ct);
    }

    public async Task DeleteAsync(string taskId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.TaskLogs.Where(x => x.TaskId == taskId).ExecuteDeleteAsync(ct);
    }
}
