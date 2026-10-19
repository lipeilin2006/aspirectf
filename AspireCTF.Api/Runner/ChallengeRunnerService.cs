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
    private readonly PlatformSettingsService _platformSettings;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _publishGates = new();

    public ChallengeRunnerService(
        ChallengeTaskQueue queue,
        ITaskStore store,
        ISandboxExecutor executor,
        WorkspaceService workspaces,
        TaskBroadcaster broadcaster,
        DynamicConcurrencyGate concurrency,
        SandboxPortLease ports,
        PlatformSettingsService platformSettings,
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
        _platformSettings = platformSettings;
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
            var prompt = BuildPrompt(task);
            var servePort = _ports.Acquire(taskId);
            var env = BuildEnv(task, prompt, servePort);
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

    private string BuildPrompt(ChallengeTask task)
    {
        var template = string.IsNullOrWhiteSpace(task.Prompt) ? _options.PromptTemplate : task.Prompt;
        var descEmbedded = template.Contains("{3", StringComparison.Ordinal);
        string prompt;
        try
        {
            prompt = string.Format(template, task.Name, task.Category, task.TargetUrl, task.Description);
        }
        catch (FormatException)
        {
            // 模板含非法大括号：退回内置模板（含 {3}），保证题目详情仍嵌入正文。
            descEmbedded = true;
            try
            {
                prompt = string.Format(_options.PromptTemplate, task.Name, task.Category, task.TargetUrl, task.Description);
            }
            catch (FormatException)
            {
                prompt = $"{_options.PromptTemplate}\n\n【题目详情】\n{task.Description}";
            }
        }

        // 老模板没有 {3} 占位符时，题目详情仍附加到末尾，保证不丢失。
        if (!descEmbedded && !string.IsNullOrWhiteSpace(task.Description))
            prompt += $"\n\n【题目详情】\n{task.Description}";

        if (!string.IsNullOrWhiteSpace(task.AdditionalPrompt))
            prompt += $"\n\n【追加信息】\n{task.AdditionalPrompt.Trim()}";

        return prompt;
    }

    private Dictionary<string, string> BuildEnv(ChallengeTask task, string prompt, int servePort)
    {
        var env = new Dictionary<string, string>
        {
            ["TASK_ID"] = task.Id,
            ["TASK_NAME"] = task.Name,
            ["TARGET_URL"] = task.TargetUrl,
            ["PROMPT"] = prompt,
            ["FLAG_PATH"] = "/workspace/flag.txt",
            ["OPENCODE_SERVE_PORT"] = servePort.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        var config = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_JSON");
        if (!string.IsNullOrWhiteSpace(config)) env["OPENCODE_CONFIG_CONTENT"] = config;

        // 静态白名单 + 设置页动态配置的 Provider Key 全部透传进沙箱。
        var passthrough = new HashSet<string>(_options.PassthroughEnvKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _platformSettings.ConfiguredEnvVarNames) passthrough.Add(key);
        foreach (var key in passthrough)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                env[key] = value;
            }
        }

        // 任务指定的模型优先于服务端全局 OPENCODE_MODEL。
        if (!string.IsNullOrWhiteSpace(task.Model))
        {
            env["OPENCODE_MODEL"] = task.Model.Trim();
        }

        if (!string.IsNullOrWhiteSpace(task.SessionId))
        {
            env["OPENCODE_SESSION_ID"] = task.SessionId;
        }

        // Always override the sandbox script default. A value of zero means
        // OpenCode itself is governed only by the task-level timeout.
        env["OPENCODE_TIMEOUT_SECONDS"] = Math.Max(0, _options.OpenCodeTimeoutSeconds).ToString();

        return env;
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
            var evt = ParseSandboxEvent(line);
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

    /// <summary>
    /// run-challenge.sh 将每个 SSE 事件转成一行 `EVT {json}`。这里把它拆成
    /// 结构化 Part（供前端按官方 part 模型渲染 Markdown / 工具卡片），
    /// Text 保留人类可读摘要（旧数据兼容 / 日志检索）。
    /// </summary>
    private static SandboxLogEvent ParseSandboxEvent(SandboxLogEvent line)
    {
        if (line.Part is not null || !line.Text.StartsWith("EVT ", StringComparison.Ordinal))
        {
            return line;
        }

        var json = line.Text[4..].Trim();
        if (json.Length == 0)
        {
            return line;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("k", out var k) ? k.GetString() : null;
            var summary = kind switch
            {
                "text" => $"[assistant] {Truncate(SafeString(root, "text"))}",
                "reasoning" => $"[思考] {Truncate(SafeString(root, "text"))}",
                "tool" => BuildToolSummary(root),
                "error" => $"[error] {SafeString(root, "err")}",
                _ => null
            };

            if (summary is null)
            {
                return line;
            }

            return new SandboxLogEvent(line.Stream, summary, line.At, json);
        }
        catch (JsonException)
        {
            return line;
        }

        static string SafeString(JsonElement element, string name)
            => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static string Truncate(string text)
        {
            text = text.ReplaceLineEndings(" ").Trim();
            return text.Length <= 120 ? text : text[..120] + "…";
        }

        static string BuildToolSummary(JsonElement root)
        {
            var tool = SafeString(root, "tool");
            var status = SafeString(root, "status");
            var label = SafeString(root, "label");
            var err = SafeString(root, "err");
            var head = string.IsNullOrWhiteSpace(label) ? tool : $"{tool}: {label}";
            return status switch
            {
                "done" => $"[tool] {Truncate(head)} ✓",
                "error" => $"[tool] {Truncate(head)} ✗ {Truncate(err)}",
                _ => $"[tool] {Truncate(head)} …"
            };
        }
    }
}
