using System.Text.Json.Serialization;
using AspireCTF.Api.Endpoints;
using AspireCTF.Api.Hubs;
using AspireCTF.Api.Queue;
using AspireCTF.Api.Runner;
using AspireCTF.Api.Sandbox;
using AspireCTF.Api.Services;
using AspireCTF.Api.State;
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
   .MapCatalogEndpoints(app.Configuration)
   .MapTaskEndpoints();

app.Run();
