using System.Text.Json.Serialization;
using AspireCTF.Api.Endpoints;
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
builder.Services.AddSingleton<TaskSubmissionService>();

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
builder.Services.AddSingleton<ChallengePromptBuilder>();
builder.Services.AddSingleton<SandboxEnvironmentBuilder>();
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
api.MapSettingsEndpoints()
   .MapSystemEndpoints()
   .MapCatalogEndpoints(app.Configuration);

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

api.MapPost("/tasks", async (SubmitTaskRequest request, TaskSubmissionService submission, CancellationToken ct) =>
    await submission.SubmitAsync(request, null, ct));

api.MapPost("/tasks/upload", async (HttpRequest httpRequest,
    TaskSubmissionService submission, CancellationToken ct) =>
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
