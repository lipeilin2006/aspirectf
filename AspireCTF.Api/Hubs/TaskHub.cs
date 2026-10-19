using AspireCTF.Api.Models;
using AspireCTF.Api.Sandbox;
using AspireCTF.Api.Services;
using AspireCTF.Api.State;
using Microsoft.AspNetCore.SignalR;

namespace AspireCTF.Api.Hubs;

public sealed class TaskHub : Hub
{
    public static string GroupName(string taskId) => $"task:{taskId}";

    public Task Subscribe(string taskId)
        => Groups.AddToGroupAsync(Context.ConnectionId, GroupName(taskId));

    public Task Unsubscribe(string taskId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(taskId));
}

/// <summary>Pushes task state changes and log lines to all connected Blazor circuits.</summary>
public sealed class TaskBroadcaster(
    IHubContext<TaskHub> hub,
    ITaskStore store,
    LogBufferService logs,
    PersistentTaskLogService persisted,
    ILogger<TaskBroadcaster> logger)
{
    public async Task LogAsync(string taskId, SandboxLogEvent line, CancellationToken ct = default)
    {
        var dto = logs.Append(taskId, line.Stream.ToString(), line.Text, line.Part);
        try
        {
            // Push to the connected session first. Database persistence must
            // not add latency to the live OpenCode conversation stream.
            await hub.Clients.Group(TaskHub.GroupName(taskId))
                .SendAsync("Log", taskId, dto, ct);
            await hub.Clients.All.SendAsync("Log", taskId, dto, ct);
        }
        catch (Exception ex)
        {
            // The ring buffer is already updated; a disconnected SignalR client
            // must not break task execution or hide logs from polling.
            logger.LogDebug(ex, "SignalR log broadcast failed for task {TaskId}", taskId);
        }

        _ = PersistLogAsync(taskId, dto);
    }

    private async Task PersistLogAsync(string taskId, LogLineDto dto)
    {
        try { await persisted.SaveAsync(taskId, dto, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Persisting task log failed for {TaskId}", taskId); }
    }

    public async Task TaskUpdatedAsync(ChallengeTask task, CancellationToken ct = default)
    {
        var snapshot = logs.Snapshot(task.Id);
        var dto = task.ToDto(snapshot.Count > 0 ? snapshot[^1].Seq : 0, snapshot.Count);
        await store.SaveAsync(task, ct);
        await hub.Clients.All.SendAsync("TaskUpdated", dto, ct);

        if (task.Status is ChallengeTaskStatus.Succeeded or ChallengeTaskStatus.Failed
            or ChallengeTaskStatus.Cancelled or ChallengeTaskStatus.TimedOut)
        {
            logger.LogInformation("任务 {TaskId} 结束: {Status} Flag={Flag}", task.Id, task.Status, task.Flag);
        }
    }
}
