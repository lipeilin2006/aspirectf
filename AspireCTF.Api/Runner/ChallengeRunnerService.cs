using System.Collections.Concurrent;
using System.Text.Json;
using AspireCTF.Api.Hubs;
using AspireCTF.Api.Models;
using AspireCTF.Api.Queue;
using AspireCTF.Api.Sandbox;
using AspireCTF.Api.Services;
using AspireCTF.Api.State;
using Microsoft.Extensions.Options;

namespace AspireCTF.Api.Runner;

/// <summary>
/// 核心调度循环：从队列取任务 → 并发闸门限流 → 一题一常驻沙箱容器执行 →
/// 实时推送结构化日志 / 捕获 Flag。容器与工作区在任务删除前保持常驻。
/// </summary>
public sealed class ChallengeRunnerService : BackgroundService
{
    private readonly ChallengeTaskQueue _queue;
    private readonly ITaskStore _store;
    private readonly ISandboxExecutor _executor;
    private readonly WorkspaceService _workspaces;
    private readonly TaskBroadcaster _broadcaster;
    private readonly SandboxOptions _options;
    private readonly ILogger<ChallengeRunnerService> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly DynamicConcurrencyGate _concurrency;
    private readonly SandboxPortLease _ports;
    private readonly ChallengePromptBuilder _promptBuilder;
    private readonly SandboxEnvironmentBuilder _environmentBuilder;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _publishGates = new();

    public ChallengeRunnerService(
        ChallengeTaskQueue queue,
        ITaskStore store,
        ISandboxExecutor executor,
        WorkspaceService workspaces,
        TaskBroadcaster broadcaster,
        DynamicConcurrencyGate concurrency,
        SandboxPortLease ports,
        ChallengePromptBuilder promptBuilder,
        SandboxEnvironmentBuilder environmentBuilder,
        IOptions<SandboxOptions> options,
        ILogger<ChallengeRunnerService> logger)
    {
        _queue = queue;
        _store = store;
        _executor = executor;
        _workspaces = workspaces;
        _broadcaster = broadcaster;
        _options = options.Value;
        _logger = logger;
        _concurrency = concurrency;
        _ports = ports;
        _promptBuilder = promptBuilder;
        _environmentBuilder = environmentBuilder;
    }

    public int RunningCount => _running.Count;

    public bool TryCancel(string taskId)
    {
        if (!_running.TryGetValue(taskId, out var cts))
        {
            return false;
        }

        cts.Cancel();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "调度器已启动：最大并发 {Max}，队列上限 {Queue}，单题超时 {Timeout} 分钟，执行器 {Executor}",
            _options.MaxConcurrent, _options.MaxQueueLength, _options.TaskTimeoutMinutes, _executor.Name);

        // Requeue interrupted work after an API restart. The workspace and
        // OpenCode session data are persisted on the workspace volume.
        var persistedTasks = await _store.ListAsync(stoppingToken);
        foreach (var task in persistedTasks.Where(x => x.Status is ChallengeTaskStatus.Running or ChallengeTaskStatus.Queued))
        {
            task.Status = ChallengeTaskStatus.Pending;
            task.Error = "API 重启后恢复任务";
            await _store.SaveAsync(task, stoppingToken);
            _queue.TryEnqueue(task.Id);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            string taskId;
            try
            {
                taskId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _logger.LogInformation("任务 {TaskId} 已出队，等待并发槽位（运行中 {Running}/{Max}）",
                taskId, _running.Count, _options.MaxConcurrent);
            var lease = await _concurrency.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("任务 {TaskId} 开始执行", taskId);
                    await RunTaskAsync(taskId, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "任务 {TaskId} 执行异常", taskId);
                }
                finally
                {
                    lease.Dispose();
                }
            }, CancellationToken.None);
        }
    }

    private async Task RunTaskAsync(string taskId, CancellationToken stoppingToken)
    {
        var task = await _store.GetAsync(taskId, stoppingToken);
        if (task is null || task.Status is ChallengeTaskStatus.Cancelled)
        {
            _logger.LogWarning("Task {TaskId} skipped: store lookup returned {Reason}", taskId, task is null ? "null" : "cancelled");
            return;
        }

        _logger.LogInformation("Task {TaskId} loaded from store: status={Status}", taskId, task.Status);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _running[taskId] = cts;
        cts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.TaskTimeoutMinutes)));

        try
        {
            task.Status = ChallengeTaskStatus.Running;
            task.StartedAt = DateTimeOffset.UtcNow;
            await _executor.IsAvailableAsync(stoppingToken); // 触发执行器探测并缓存结果
            task.Executor = _executor.Name;
            await _broadcaster.TaskUpdatedAsync(task, stoppingToken);
            _logger.LogInformation("Task {TaskId} marked Running, building sandbox request", taskId);

            var workspace = Path.Combine(_workspaces.Root, task.Id);
            var prompt = _promptBuilder.Build(task);
            var servePort = _ports.Acquire(taskId);
            var env = _environmentBuilder.Build(task, prompt, servePort);
            _logger.LogInformation("Task {TaskId} prompt/env ready, workspace={Workspace} opencodePort={Port}",
                taskId, workspace, servePort);

            // 把实际送入 OpenCode 的 Prompt 作为结构化 Part 推送到会话流，
            // 前端折叠展示（run-challenge.sh 的 PROMPT 环境变量即此内容）。
            await PublishLogAsync(taskId, new SandboxLogEvent(SandboxLogStream.System,
                $"[prompt] 实际输入 Prompt（{prompt.Length} 字符）",
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new { k = "prompt", text = prompt })));

            // Deterministic name so that a requeued task (API restart /
            // continue) reuses the same persistent container instead of
            // orphaning a new one each run.
            var containerName = $"ctf-sandbox-{task.Id}";
            if (containerName.Length > 63)
            {
                containerName = containerName[..63];
            }

            task.ContainerName = containerName;
            await _broadcaster.TaskUpdatedAsync(task, stoppingToken);

            var request = new SandboxRunRequest(
                ContainerName: containerName,
                WorkspaceHostPath: workspace,
                Prompt: prompt,
                Env: env,
                Timeout: TimeSpan.FromMinutes(Math.Max(1, _options.TaskTimeoutMinutes)));

            var progress = new ProgressHandler(line => _ = PublishLogAsync(taskId, line));

            _logger.LogInformation("Task {TaskId} invoking executor {Executor}", taskId, _executor.Name);
            var result = await _executor.RunAsync(request, progress, cts.Token);
            _logger.LogInformation("Task {TaskId} executor finished: success={Success} exit={Exit} flag={Flag} error={Error}",
                taskId, result.Success, result.ExitCode, result.Flag, result.Error);

            task.ExitCode = result.ExitCode;
            task.Flag = result.Flag ?? task.Flag;
            task.FailureReason = result.Error;
            task.SessionId = result.SessionId ?? task.SessionId;
            task.Writeup = result.Writeup ?? task.Writeup;
            task.CompletedAt = DateTimeOffset.UtcNow;

            if (task.Flag is not null)
            {
                task.Status = ChallengeTaskStatus.Succeeded;
                task.Error = null;
            }
            else if (stoppingToken.IsCancellationRequested)
            {
                task.Status = ChallengeTaskStatus.Cancelled;
            }
            else if (cts.IsCancellationRequested)
            {
                task.Status = task.Status == ChallengeTaskStatus.Cancelled
                    ? ChallengeTaskStatus.Cancelled
                    : ChallengeTaskStatus.TimedOut;
                task.Error ??= result.Error ?? "任务超时";
            }
            else
            {
                task.Status = ChallengeTaskStatus.Failed;
                task.Error = result.Error ?? "未知错误";
            }

            await _broadcaster.TaskUpdatedAsync(task, stoppingToken);
            // 沙箱容器与工作区保持常驻：会话历史与 OpenCode 数据随任务保留，
            // 仅在用户删除任务时（DELETE /api/tasks/{id}）才移除容器与目录。
        }
        catch (OperationCanceledException)
        {
            task.Status = stoppingToken.IsCancellationRequested
                ? ChallengeTaskStatus.Cancelled
                : task.Status == ChallengeTaskStatus.Cancelled
                    ? ChallengeTaskStatus.Cancelled
                    : ChallengeTaskStatus.TimedOut;
            task.CompletedAt = DateTimeOffset.UtcNow;
            await _broadcaster.TaskUpdatedAsync(task, CancellationToken.None);
        }
        finally
        {
            _ports.Release(taskId);
            _running.TryRemove(taskId, out _);
        }
    }


    private sealed class ProgressHandler(Action<SandboxLogEvent> handler) : IProgress<SandboxLogEvent>
    {
        public void Report(SandboxLogEvent value) => handler(value);
    }

    private async Task PublishLogAsync(string taskId, SandboxLogEvent line)
    {
        // Per-task semaphore keeps fire-and-forget publishes in order, so
        // streaming text deltas cannot interleave out of sequence.
        var gate = _publishGates.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            var evt = SandboxLogEventParser.Parse(line);
            await _broadcaster.LogAsync(taskId, evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish sandbox log for task {TaskId}", taskId);
        }
        finally
        {
            gate.Release();
        }
    }

}
