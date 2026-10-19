using AspireCTF.Api.Models;
using AspireCTF.Api.Queue;
using AspireCTF.Api.Runner;
using AspireCTF.Api.Sandbox;
using AspireCTF.Api.Services;
using AspireCTF.Api.State;
using Microsoft.Extensions.Options;

namespace AspireCTF.Api.Endpoints;

public static class SystemEndpoints
{
    public static RouteGroupBuilder MapSystemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/system/info", async (
            IOptions<SandboxOptions> options,
            ITaskStore store,
            ISandboxExecutor executor,
            ChallengeTaskQueue queue,
            ChallengeRunnerService runner,
            PostgresTaskStore postgres,
            DynamicConcurrencyGate concurrency,
            SandboxReadinessService sandbox,
            CancellationToken ct) =>
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

        return api;
    }
}
