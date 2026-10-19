using System.Text.Json.Serialization;
using AspireCTF.Api.Hubs;
using AspireCTF.Api.Models;
using AspireCTF.Api.Queue;
using AspireCTF.Api.Runner;
using AspireCTF.Api.Sandbox;
using AspireCTF.Api.Services;
using AspireCTF.Api.State;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(builder.Environment.ContentRootPath, "data-protection-keys")));
builder.Services.Configure<SandboxOptions>(builder.Configuration.GetSection(SandboxOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var postgresConnection = builder.Configuration.GetConnectionString("ctfdb");
if (string.IsNullOrWhiteSpace(postgresConnection))
{
    throw new InvalidOperationException("缺少 PostgreSQL 连接串 ConnectionStrings:ctfdb");
}

builder.Services.AddDbContextFactory<TaskDbContext>(options => options.UseNpgsql(postgresConnection));
builder.Services.AddSingleton<PostgresTaskStore>();
builder.Services.AddSingleton<ITaskStore>(sp => sp.GetRequiredService<PostgresTaskStore>());
builder.Services.AddSingleton<ChallengeTaskQueue>();
builder.Services.AddSingleton<DynamicConcurrencyGate>(sp =>
    new DynamicConcurrencyGate(sp.GetRequiredService<IOptions<SandboxOptions>>().Value.MaxConcurrent));
builder.Services.AddSingleton<PlatformSettingsService>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<ModelsDevCatalogService>();
builder.Services.AddSingleton<OpenCodeServerService>();
builder.Services.AddHttpClient("modelsdev", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("AspireCTF/1.0");
});
builder.Services.AddSingleton<SandboxReadinessService>();
builder.Services.AddSingleton<SandboxPortLease>();
builder.Services.AddSingleton<LogBufferService>();
builder.Services.AddSingleton<PersistentTaskLogService>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<TaskBroadcaster>();

builder.Services.AddSingleton<DockerApiSandboxExecutor>();
builder.Services.AddSingleton<PodmanCliSandboxExecutor>();
builder.Services.AddSingleton<SimulationExecutor>();
builder.Services.AddSingleton<ISandboxExecutor>(sp =>
{
    var options = sp.GetRequiredService<IOptions<SandboxOptions>>().Value;
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var dockerApi = sp.GetRequiredService<DockerApiSandboxExecutor>();
    var podmanCli = sp.GetRequiredService<PodmanCliSandboxExecutor>();
    var simulation = sp.GetRequiredService<SimulationExecutor>();

    var provider = options.Provider.ToLowerInvariant() switch
    {
        "dockerapi" or "podmanapi" or "api" => (ISandboxExecutor)dockerApi,
        "podmancli" or "cli" => podmanCli,
        "simulation" or "sim" => simulation,
        _ => new ChainedSandboxExecutor(
            options.AllowSimulation
                ? new ISandboxExecutor[] { dockerApi, podmanCli, simulation }
                : new ISandboxExecutor[] { dockerApi, podmanCli },
            sp.GetRequiredService<ILogger<ChainedSandboxExecutor>>())
    };
    logger.LogInformation("沙箱 Provider 配置: {Provider} → {Executor}", options.Provider, provider.Name);
    return provider;
});

builder.Services.AddSingleton<ChallengeRunnerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChallengeRunnerService>());

var corsAllowAny = builder.Configuration.GetValue("Cors:AllowAny", true);
if (corsAllowAny)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowAnyOrigin()));
}

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<PostgresTaskStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<PlatformSettingsService>().InitializeAsync();
}

app.UseCors();
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapHub<TaskHub>("/hubs/tasks");

var api = app.MapGroup("/api");

api.MapGet("/settings", (PlatformSettingsService settings) => Results.Ok(settings.ToDto(settings.Current)));
api.MapGet("/settings/providers", async (ModelsDevCatalogService catalog, CancellationToken ct) =>
    Results.Ok(await catalog.GetProvidersAsync(ct)));
api.MapGet("/settings/api-keys", async (PlatformSettingsService settings, CancellationToken ct) => Results.Ok(await settings.GetApiKeysAsync(ct)));
api.MapPost("/settings/api-keys", async (ApiKeyUpdateRequest request, PlatformSettingsService settings, ModelCatalogService models, CancellationToken ct) =>
{
    var saved = await settings.SetApiKeyAsync(request.Provider, request.ApiKey, ct);
    if (!saved) return Results.BadRequest("Provider 或 API Key 无效");
    models.Invalidate();
    return Results.Ok(await settings.GetApiKeysAsync(ct));
});
api.MapDelete("/settings/api-keys/{provider}", async (string provider, PlatformSettingsService settings, ModelCatalogService models, CancellationToken ct) =>
{
    var removed = await settings.RemoveApiKeyAsync(provider, ct);
    if (!removed) return Results.NotFound();
    models.Invalidate();
    return Results.Ok(await settings.GetApiKeysAsync(ct));
});
api.MapGet("/settings/models", async (ModelCatalogService models, CancellationToken ct) => Results.Ok(await models.GetAsync(ct)));
api.MapGet("/settings/opencode/validate", async (OpenCodeServerService opencode, CancellationToken ct) => Results.Ok(await opencode.ValidateAsync(ct)));
api.MapGet("/settings/opencode/cli-models", async (OpenCodeServerService opencode, CancellationToken ct) => Results.Ok(await opencode.GetModelsAsync(ct)));
api.MapPut("/settings", async (PlatformSettingsDto dto, PlatformSettingsService settings, CancellationToken ct) =>
    Results.Ok(settings.ToDto(await settings.UpdateAsync(dto, ct))));

api.MapGet("/challenges", (IConfiguration configuration) =>
{
    var entries = configuration.GetSection("Catalog:Challenges").Get<ChallengeCatalogEntry[]>() ?? [];
    return Results.Ok(entries);
});

api.MapGet("/system/info", async (IOptions<SandboxOptions> options, ITaskStore store,
    ISandboxExecutor executor, ChallengeTaskQueue queue, ChallengeRunnerService runner,
    [FromServices] PostgresTaskStore postgres, DynamicConcurrencyGate concurrency,
    SandboxReadinessService sandbox, CancellationToken ct) =>
{
    var tasks = await store.ListAsync(ct);
    var chained = executor as ChainedSandboxExecutor;
    var readiness = sandbox.GetStatus();
    return Results.Ok(new SystemInfoDto
    {
        Executor = executor.Name,
        ExecutorAvailable = (await executor.IsAvailableAsync(ct)) ? "yes" : "no",
        AvailableExecutors = chained?.AvailableNames ?? [executor.Name],
        MaxConcurrent = concurrency.Limit,
        Running = runner.RunningCount,
        QueueLength = queue.Length,
        TotalTasks = tasks.Count,
        PostgresConnected = postgres.DatabaseConnected,
        SandboxImage = options.Value.Image,
        SandboxReady = readiness.Ready,
        SandboxStatus = readiness.Status,
        SandboxMessage = readiness.Message,
        SandboxBuildOutput = readiness.Output.ToArray()
    });
});

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

api.MapGet("/tasks/{id}/logs", async (string id, long? afterSeq, LogBufferService logs, PersistentTaskLogService persisted, CancellationToken ct) =>
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

api.MapPost("/tasks/{id}/continue", async (string id, ContinueTaskRequest request, string? user, ITaskStore store,
    ChallengeTaskQueue queue, TaskBroadcaster broadcaster, CancellationToken ct) =>
{
    var task = await store.GetAsync(id, ct);
    if (task is null) return Results.NotFound();
    if (!CanManageTask(task, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
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

api.MapPost("/tasks", async (SubmitTaskRequest request,
    ITaskStore store, ChallengeTaskQueue queue, WorkspaceService workspaces,
    TaskBroadcaster broadcaster, IConfiguration configuration, SandboxReadinessService sandbox, CancellationToken ct) =>
    await SubmitAsync(request, null, store, queue, workspaces, broadcaster, configuration, sandbox, ct));

api.MapPost("/tasks/upload", async (HttpRequest httpRequest,
    ITaskStore store, ChallengeTaskQueue queue, WorkspaceService workspaces,
    TaskBroadcaster broadcaster, IConfiguration configuration, SandboxReadinessService sandbox, CancellationToken ct) =>
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
    return await SubmitAsync(request, file?.OpenReadStream(), store, queue, workspaces, broadcaster, configuration, sandbox, ct);
});

api.MapDelete("/tasks/{id}", async (string id, string? user, ITaskStore store, ChallengeRunnerService runner,
    TaskBroadcaster broadcaster, WorkspaceService workspaces, LogBufferService logs, PersistentTaskLogService persisted,
    ISandboxExecutor executor, CancellationToken ct) =>
{
    var task = await store.GetAsync(id, ct);
    if (task is null)
    {
        return Results.NotFound();
    }
    if (!CanManageTask(task, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);

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
    var task = await store.GetAsync(id, ct);
    if (task is null) return Results.NotFound();
    if (!CanManageTask(task, user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (task.Status is not (ChallengeTaskStatus.Pending or ChallengeTaskStatus.Queued or ChallengeTaskStatus.Running))
        return Results.BadRequest("任务当前不可取消");

    task.Status = ChallengeTaskStatus.Cancelled;
    task.CompletedAt = DateTimeOffset.UtcNow;
    runner.TryCancel(id);
    await broadcaster.TaskUpdatedAsync(task, ct);
    return Results.Ok(task.ToDto());
});

app.Run();

// 任务创建者或管理员可操作（continue / cancel / delete）。
static bool CanManageTask(ChallengeTask task, string? user)
    => string.Equals(task.CreatedBy, user, StringComparison.OrdinalIgnoreCase)
       || string.Equals(user, "admin", StringComparison.OrdinalIgnoreCase);

async Task<IResult> SubmitAsync(
    SubmitTaskRequest request,
    Stream? zipStream,
    ITaskStore store,
    ChallengeTaskQueue queue,
    WorkspaceService workspaces,
    TaskBroadcaster broadcaster,
    IConfiguration configuration,
    SandboxReadinessService sandbox,
    CancellationToken ct)
{
    var readiness = sandbox.GetStatus();
    if (!readiness.Ready)
    {
        return Results.Json(new { error = readiness.Message, status = readiness.Status }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    if (string.IsNullOrWhiteSpace(request.Model))
    {
        return Results.BadRequest("必须选择模型");
    }
    if (string.IsNullOrWhiteSpace(request.Name) && string.IsNullOrWhiteSpace(request.ChallengeId))
    {
        return Results.BadRequest("必须提供 name 或 challengeId");
    }

    ChallengeCatalogEntry? catalogEntry = null;
    string? catalogSourcePath = null;
    if (!string.IsNullOrWhiteSpace(request.ChallengeId))
    {
        var catalog = configuration.GetSection("Catalog:Challenges").Get<ChallengeCatalogEntry[]>() ?? [];
        catalogEntry = catalog.FirstOrDefault(c => string.Equals(c.Id, request.ChallengeId, StringComparison.OrdinalIgnoreCase));
        if (catalogEntry is null)
        {
            return Results.BadRequest($"题库中不存在 challengeId={request.ChallengeId}");
        }

        if (!string.IsNullOrWhiteSpace(catalogEntry.SourcePath))
        {
            var samplesRoot = configuration["Catalog:SamplesPath"] ?? "/opt/catalog";
            var candidate = Path.GetFullPath(Path.Combine(samplesRoot, catalogEntry.SourcePath));
            if (Directory.Exists(candidate))
            {
                catalogSourcePath = candidate;
            }
        }
    }

    var task = new ChallengeTask
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
        HasSource = zipStream is not null || catalogSourcePath is not null,
        Status = ChallengeTaskStatus.Pending
    };

    try
    {
        await workspaces.PrepareAsync(task, zipStream, catalogSourcePath, ct);
    }
    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
    {
        return Results.BadRequest(ex.Message);
    }

    // 先持久化再入队：runner 消费极快，若先入队会出现 GetAsync 竞态返回 null
    task.Status = ChallengeTaskStatus.Pending;
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
    app.Logger.LogInformation("新任务 {TaskId} 已入队：{Name} → {Target}", task.Id, task.Name, task.TargetUrl);

    return Results.Accepted($"/api/tasks/{task.Id}", task.ToDto());
}
