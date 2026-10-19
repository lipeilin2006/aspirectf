using AspireCTF.Api.Hubs;
using AspireCTF.Api.Models;
using AspireCTF.Api.Queue;
using AspireCTF.Api.Sandbox;
using AspireCTF.Api.State;

namespace AspireCTF.Api.Services;

/// <summary>Validates, prepares, persists and queues newly submitted tasks.</summary>
public sealed class TaskSubmissionService(
    ITaskStore store,
    ChallengeTaskQueue queue,
    WorkspaceService workspaces,
    TaskBroadcaster broadcaster,
    IConfiguration configuration,
    SandboxReadinessService sandbox,
    ILogger<TaskSubmissionService> logger)
{
    public async Task<IResult> SubmitAsync(
        SubmitTaskRequest request,
        Stream? zipStream,
        CancellationToken ct)
    {
        var readiness = sandbox.GetStatus();
        if (!readiness.Ready)
        {
            return Results.Json(new { error = readiness.Message, status = readiness.Status },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(request.Model))
            return Results.BadRequest("必须选择模型");
        if (string.IsNullOrWhiteSpace(request.Name) && string.IsNullOrWhiteSpace(request.ChallengeId))
            return Results.BadRequest("必须提供 name 或 challengeId");

        var catalogEntry = FindCatalogEntry(request.ChallengeId);
        if (!string.IsNullOrWhiteSpace(request.ChallengeId) && catalogEntry is null)
            return Results.BadRequest($"题库中不存在 challengeId={request.ChallengeId}");

        var catalogSourcePath = ResolveCatalogSourcePath(catalogEntry);
        var task = CreateTask(request, catalogEntry, zipStream is not null || catalogSourcePath is not null);

        try
        {
            await workspaces.PrepareAsync(task, zipStream, catalogSourcePath, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            return Results.BadRequest(ex.Message);
        }

        // Persist before enqueueing: the runner can consume an item immediately.
        await store.SaveAsync(task, ct);
        if (!queue.TryEnqueue(task.Id))
        {
            task.Status = ChallengeTaskStatus.Failed;
            task.Error = "任务队列已满，请稍后再试";
            task.CompletedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(task, ct);
            workspaces.Cleanup(task.Id);
            return Results.Json(task.ToDto(), statusCode: StatusCodes.Status429TooManyRequests);
        }

        task.Status = ChallengeTaskStatus.Queued;
        await broadcaster.TaskUpdatedAsync(task, ct);
        logger.LogInformation("新任务 {TaskId} 已入队：{Name} → {Target}", task.Id, task.Name, task.TargetUrl);
        return Results.Accepted($"/api/tasks/{task.Id}", task.ToDto());
    }

    private ChallengeCatalogEntry? FindCatalogEntry(string? challengeId)
    {
        if (string.IsNullOrWhiteSpace(challengeId))
            return null;

        var catalog = configuration.GetSection("Catalog:Challenges").Get<ChallengeCatalogEntry[]>() ?? [];
        return catalog.FirstOrDefault(c => string.Equals(c.Id, challengeId, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveCatalogSourcePath(ChallengeCatalogEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(entry?.SourcePath))
            return null;

        var samplesRoot = configuration["Catalog:SamplesPath"] ?? "/opt/catalog";
        var candidate = Path.GetFullPath(Path.Combine(samplesRoot, entry.SourcePath));
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static ChallengeTask CreateTask(SubmitTaskRequest request, ChallengeCatalogEntry? catalogEntry, bool hasSource)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name ?? catalogEntry?.Name ?? catalogEntry?.Id ?? "unnamed",
            CreatedBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? "admin" : request.CreatedBy.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? catalogEntry?.Category ?? "web" : request.Category,
            Description = string.IsNullOrWhiteSpace(request.Description) ? catalogEntry?.Description ?? "" : request.Description,
            TargetUrl = string.IsNullOrWhiteSpace(request.TargetUrl) ? catalogEntry?.TargetUrl ?? "" : request.TargetUrl,
            Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? catalogEntry?.DefaultPrompt ?? "" : request.Prompt,
            Model = request.Model?.Trim() ?? "",
            ChallengeId = request.ChallengeId ?? "",
            HasSource = hasSource,
            Status = ChallengeTaskStatus.Pending
        };
}
