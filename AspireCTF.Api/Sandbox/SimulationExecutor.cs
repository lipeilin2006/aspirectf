using AspireCTF.Api.Services;
using Microsoft.Extensions.Options;

namespace AspireCTF.Api.Sandbox;

/// <summary>模拟执行器：不依赖容器引擎，用于在无 Docker/Podman 或无 AI Key 时验证全流程管道。</summary>
public sealed class SimulationExecutor : ISandboxExecutor
{
    private readonly SandboxOptions _options;

    public SimulationExecutor(IOptions<SandboxOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "simulation";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(_options.AllowSimulation);

    public async Task<SandboxRunResult> RunAsync(
        SandboxRunRequest request,
        IProgress<SandboxLogEvent> progress,
        CancellationToken ct)
    {
        var steps = new[]
        {
            "[simulation] 启动 OpenCode CLI（模拟）...",
            $"[simulation] 目标: {request.Env.GetValueOrDefault("TARGET_URL", "(未设置)")}",
            "[simulation] 审计 /workspace 中的题目源码",
            "[simulation] 发现疑似注入点，编写 exploit.py",
            "[simulation] 执行 exploit.py 攻击目标",
            $"[simulation] 获取到 flag: {_options.SimulatedFlag}"
        };

        foreach (var step in steps)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
            progress.Report(new SandboxLogEvent(SandboxLogStream.Stdout, step, DateTimeOffset.UtcNow));
        }

        string? writeup = null;
        try
        {
            var flagPath = Path.Combine(request.WorkspaceHostPath, "flag.txt");
            await File.WriteAllTextAsync(flagPath, _options.SimulatedFlag, ct);
            writeup = $"""
                # Writeup（模拟）

                - 目标：{request.Env.GetValueOrDefault("TARGET_URL", "(未设置)")}
                - 步骤：审计源码 → 注入点 → exploit.py → 获取 flag
                - flag：{_options.SimulatedFlag}
                """;
            await File.WriteAllTextAsync(Path.Combine(request.WorkspaceHostPath, "writeup.md"), writeup, ct);
        }
        catch
        {
            // workspace may already be cleaned up
        }

        progress.Report(new SandboxLogEvent(SandboxLogStream.Stdout, "DONE", DateTimeOffset.UtcNow));
        return new SandboxRunResult(true, 0, _options.SimulatedFlag, null, null, writeup);
    }

    public Task RemoveContainerAsync(string? containerName, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>按优先级选择第一个可用的沙箱执行器（API → CLI → Simulation）。</summary>
public sealed class ChainedSandboxExecutor : ISandboxExecutor
{
    private readonly IReadOnlyList<ISandboxExecutor> _executors;
    private readonly ILogger<ChainedSandboxExecutor> _logger;
    private ISandboxExecutor? _selected;

    public ChainedSandboxExecutor(IEnumerable<ISandboxExecutor> executors, ILogger<ChainedSandboxExecutor> logger)
    {
        _executors = executors.ToList();
        _logger = logger;
    }

    public string Name => _selected?.Name ?? "auto(unresolved)";

    public string[] AvailableNames { get; private set; } = [];

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        await EnsureSelectedAsync(ct);
        return _selected is not null;
    }

    public async Task<SandboxRunResult> RunAsync(
        SandboxRunRequest request,
        IProgress<SandboxLogEvent> progress,
        CancellationToken ct)
    {
        var executor = await EnsureSelectedAsync(ct)
            ?? throw new InvalidOperationException(
                "没有可用的沙箱执行器（Podman API / podman CLI 均不可用，且未启用 AllowSimulation）");

        return await executor.RunAsync(request, progress, ct);
    }

    public async Task RemoveContainerAsync(string? containerName, CancellationToken ct = default)
    {
        var executor = await EnsureSelectedAsync(ct);
        if (executor is not null)
        {
            await executor.RemoveContainerAsync(containerName, ct);
        }
    }

    private async Task<ISandboxExecutor?> EnsureSelectedAsync(CancellationToken ct)
    {
        if (_selected is not null)
        {
            return _selected;
        }

        var available = new List<string>();
        foreach (var executor in _executors)
        {
            _logger.LogInformation("Probing sandbox executor {Executor} ...", executor.Name);
            if (await executor.IsAvailableAsync(ct))
            {
                available.Add(executor.Name);
                _selected ??= executor;
                _logger.LogInformation("Executor {Executor} available", executor.Name);
            }
            else
            {
                _logger.LogInformation("Executor {Executor} NOT available", executor.Name);
            }
        }

        AvailableNames = available.ToArray();
        if (_selected is not null)
        {
            _logger.LogInformation("沙箱执行器已选择: {Executor}（可用: {Available}）", _selected.Name, string.Join(", ", available));
            return _selected;
        }

        _logger.LogWarning("没有可用的沙箱执行器，可用列表: {Available}", string.Join(", ", available));
        return null;
    }
}
