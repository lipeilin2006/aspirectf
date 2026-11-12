using AspireCTF.Api.Hubs;
using AspireCTF.Api.Models;
using AspireCTF.Api.Queue;
using AspireCTF.Api.Runner;
using AspireCTF.Api.Sandbox;
using AspireCTF.Api.Services;
using AspireCTF.Api.State;
using AspireCTF.Contracts;

namespace AspireCTF.Api.Endpoints;

/// <summary>
/// 任务相关路由：列表 / 详情 / 日志 / Writeup / 提交（含压缩包上传）/
/// 续跑 / 取消 / 删除。生命周期与容器清理逻辑集中在 DELETE 中。
/// </summary>
public static class TaskEndpoints
{
    public static RouteGroupBuilder MapTaskEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/tasks", async (ITaskStore store, CancellationToken ct) =>
        {
            var tasks = await store.ListAsync(ct);
            return Results.Ok(tasks.Select(t => t.ToDto()));
        });

        api.MapGet("/tasks/{id}", async (string id, ITaskStore store, LogBufferService logs, CancellationToken ct) =>
        {
            var task = await store.GetAsync(id, ct);
            if (task is null)
            {
                return Results.NotFound();
            }

            var snapshot = logs.Snapshot(id);
            return Results.Ok(task.ToDto(snapshot.Count > 0 ? snapshot[^1].Seq : 0, snapshot.Count));
        });

        api.MapGet("/tasks/{id}/logs", async (string id, long? afterSeq, LogBufferService logs,
            PersistentTaskLogService persisted, CancellationToken ct) =>
        {
            var after = afterSeq ?? 0;
            var memory = logs.Snapshot(id, after);
            return Results.Ok(memory.Count > 0 ? memory : await persisted.ListAsync(id, after, ct));
        });

        api.MapGet("/tasks/{id}/writeup", async (string id, ITaskStore store, CancellationToken ct) =>
        {
            var task = await store.GetAsync(id, ct);
            if (task is null || string.IsNullOrWhiteSpace(task.Writeup))
            {
                return Results.NotFound();
            }
            return Results.File(System.Text.Encoding.UTF8.GetBytes(task.Writeup), "text/markdown", $"{task.Name}.md");
        });

        api.MapPost("/tasks/{id}/continue", async (string id, ContinueTaskRequest request, string? user,
            ITaskStore store, ChallengeTaskQueue queue, TaskBroadcaster broadcaster, CancellationToken ct) =>
        {
            var (access, task) = await FindManageableTaskAsync(id, user, store, ct);
            if (task is null) return AccessDenied(access);
            if (task.Status is ChallengeTaskStatus.Running or ChallengeTaskStatus.Queued or ChallengeTaskStatus.Pending)
                return Results.BadRequest("任务当前正在运行");
            if (string.IsNullOrWhiteSpace(request.AdditionalPrompt)) return Results.BadRequest("追加信息不能为空");

            // 追加信息单独存放，不再写入 Prompt：否则会覆盖默认模板/自定义提示词。
            task.AdditionalPrompt = string.IsNullOrWhiteSpace(task.AdditionalPrompt)
                ? request.AdditionalPrompt.Trim()
                : $"{task.AdditionalPrompt}\n{request.AdditionalPrompt.Trim()}";
            task.Status = ChallengeTaskStatus.Pending;
            task.Error = null;
            task.FailureReason = null;
            task.Flag = null;
            task.CompletedAt = null;
            await store.SaveAsync(task, ct);
            if (!queue.TryEnqueue(task.Id)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            task.Status = ChallengeTaskStatus.Queued;
            await broadcaster.TaskUpdatedAsync(task, ct);
            return Results.Accepted($"/api/tasks/{id}", task.ToDto());
        });

        api.MapPost("/tasks", async (SubmitTaskRequest request, TaskSubmissionService submission, CancellationToken ct) =>
            await submission.SubmitAsync(request, null, ct));

        api.MapPost("/tasks/upload", async (HttpRequest httpRequest, TaskSubmissionService submission,
            CancellationToken ct) =>
        {
            if (!httpRequest.HasFormContentType)
            {
                return Results.BadRequest("需要 multipart/form-data");
            }

            var form = await httpRequest.ReadFormAsync(ct);
            var request = new SubmitTaskRequest
            {
                Name = form["name"].FirstOrDefault() ?? "",
                CreatedBy = form["createdBy"].FirstOrDefault() ?? "admin",
                Category = form["category"].FirstOrDefault() ?? "web",
                Description = form["description"].FirstOrDefault() ?? "",
                TargetUrl = form["targetUrl"].FirstOrDefault() ?? "",
                Prompt = form["prompt"].FirstOrDefault() ?? "",
                Model = form["model"].FirstOrDefault() ?? "",
                ChallengeId = form["challengeId"].FirstOrDefault() ?? ""
            };

            var file = form.Files.GetFile("source");
            return await submission.SubmitAsync(request, file?.OpenReadStream(), ct);
        });

        api.MapDelete("/tasks/{id}", async (string id, string? user, ITaskStore store, ChallengeRunnerService runner,
            TaskBroadcaster broadcaster, WorkspaceService workspaces, LogBufferService logs,
            PersistentTaskLogService persisted, ISandboxExecutor executor, CancellationToken ct) =>
        {
            var (access, task) = await FindManageableTaskAsync(id, user, store, ct);
            if (task is null) return AccessDenied(access);

            if (task.Status is ChallengeTaskStatus.Pending or ChallengeTaskStatus.Queued or ChallengeTaskStatus.Running)
            {
                task.Status = ChallengeTaskStatus.Cancelled;
                task.CompletedAt = DateTimeOffset.UtcNow;
                runner.TryCancel(id);
                await broadcaster.TaskUpdatedAsync(task, ct);
                if (task.StartedAt is null)
                {
                    // 从未真正启动的排队任务：容器可能已在创建中，稍等后再清理一次。
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                    await executor.RemoveContainerAsync(task.ContainerName, CancellationToken.None);
                    workspaces.Cleanup(id);
                }
            }

            await store.DeleteAsync(id, ct);
            // 容器常驻到任务删除这一刻才移除（运行中会先终止 exec 再强删容器）。
            await executor.RemoveContainerAsync(task.ContainerName, ct);
            workspaces.Cleanup(id);
            logs.Remove(id);
            await persisted.DeleteAsync(id, ct);

            return Results.NoContent();
        });

        api.MapPost("/tasks/{id}/cancel", async (string id, string? user, ITaskStore store,
            ChallengeRunnerService runner, TaskBroadcaster broadcaster, CancellationToken ct) =>
        {
            var (access, task) = await FindManageableTaskAsync(id, user, store, ct);
            if (task is null) return AccessDenied(access);
            if (task.Status is not (ChallengeTaskStatus.Pending or ChallengeTaskStatus.Queued or ChallengeTaskStatus.Running))
                return Results.BadRequest("任务当前不可取消");

            task.Status = ChallengeTaskStatus.Cancelled;
            task.CompletedAt = DateTimeOffset.UtcNow;
            runner.TryCancel(id);
            await broadcaster.TaskUpdatedAsync(task, ct);
            return Results.Ok(task.ToDto());
        });

        return api;
    }

    private enum TaskAccess
    {
        Ok,
        NotFound,
        Forbidden
    }

    /// <summary>
    /// 取任务并校验操作权限：任务创建者或 admin 可操作（continue / cancel / delete）。
    /// 不存在与无权限分开返回，保持原有 404 / 403 语义。
    /// </summary>
    private static async Task<(TaskAccess Access, ChallengeTask? Task)> FindManageableTaskAsync(
        string id, string? user, ITaskStore store, CancellationToken ct)
    {
        var task = await store.GetAsync(id, ct);
        if (task is null)
        {
            return (TaskAccess.NotFound, null);
        }

        return CanManageTask(task, user)
            ? (TaskAccess.Ok, task)
            : (TaskAccess.Forbidden, null);
    }

    private static bool CanManageTask(ChallengeTask task, string? user)
        => string.Equals(task.CreatedBy, user, StringComparison.OrdinalIgnoreCase)
           || string.Equals(user, "admin", StringComparison.OrdinalIgnoreCase);

    private static IResult AccessDenied(TaskAccess access)
        => access == TaskAccess.NotFound
            ? Results.NotFound()
            : Results.StatusCode(StatusCodes.Status403Forbidden);
}
