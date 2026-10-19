using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace AspireCTF.Api.Sandbox;

/// <summary>
/// 通过 podman CLI（进程方式）驱动沙箱，适合本地开发机直接对接 Podman Machine，
/// 以及作为 REST API 不可用时的兜底执行通道。
/// 容器常驻（镜像入口为 sandbox-entry.sh 守护循环）：每次运行只 exec 进同一容器，
/// OpenCode 会话与工作区随容器保留；仅在任务被删除时才移除容器。
/// </summary>
public sealed class PodmanCliSandboxExecutor : ISandboxExecutor
{
    private readonly SandboxOptions _options;
    private readonly ILogger<PodmanCliSandboxExecutor> _logger;

    public PodmanCliSandboxExecutor(IOptions<SandboxOptions> options, ILogger<PodmanCliSandboxExecutor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "podman-cli";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (exit, _, _) = await RunPodmanAsync(["version"], ct);
            return exit == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<SandboxRunResult> RunAsync(
        SandboxRunRequest request,
        IProgress<SandboxLogEvent> progress,
        CancellationToken ct)
    {
        var ready = await EnsureContainerAsync(request, progress, ct);
        if (ready is not null)
        {
            return ready;
        }

        var execArgs = new List<string>
        {
            "exec", "-i",
            // opencode 与任务脚本以 root 运行，Agent 才能按需 apt/pip 装工具。
            "--user", SandboxPaths.RootUser,
            "-w", SandboxPaths.Workspace
        };

        foreach (var (key, value) in request.Env)
        {
            execArgs.AddRange(["-e", $"{key}={value}"]);
        }

        execArgs.AddRange([request.ContainerName, SandboxPaths.RunScript]);

        progress.Report(new SandboxLogEvent(SandboxLogStream.System,
            $"[executor] podman {string.Join(' ', execArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}",
            DateTimeOffset.UtcNow));

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token);
        timeoutCts.CancelAfter(request.Timeout);

        var capture = new FlagCapture(progress);

        using var execProcess = StartPodman(execArgs);
        // 捕获 Flag 仅记录，不提前终止：任务由 OpenCode 自然结束
        // （收尾三件套 flag.txt / failure.txt / writeup.md 见 AGENTS.md）。
        var pumpTask = PumpLogsAsync(execProcess, progress, line => { _ = capture.TryCapture(line); }, runCts.Token);

        int exitCode;
        try
        {
            await execProcess.WaitForExitAsync(timeoutCts.Token);
            exitCode = execProcess.ExitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = -1;
            await StopRunAsync(request.ContainerName);
        }
        finally
        {
            runCts.Cancel();
            try { execProcess.Kill(entireProcessTree: true); } catch { /* already exited */ }
            try { await pumpTask; } catch { /* ignore */ }
        }

        var timedOut = !capture.Captured && !ct.IsCancellationRequested && exitCode == -1;
        var result = SandboxRunOutcome.Finalize(request, exitCode, capture.Flag, timedOut, ct.IsCancellationRequested);
        if (result.Flag is not null && !capture.Captured)
        {
            progress.Report(new SandboxLogEvent(SandboxLogStream.System,
                "[executor] 从 /workspace/flag.txt 捕获 Flag", DateTimeOffset.UtcNow));
        }

        return result;
    }

    public async Task RemoveContainerAsync(string? containerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            return;
        }

        try
        {
            var (exit, _, err) = await RunPodmanAsync(["rm", "-f", containerName], ct);
            if (exit == 0)
            {
                _logger.LogInformation("沙箱容器已移除: {Container}", containerName);
            }
            else
            {
                _logger.LogWarning("移除沙箱容器 {Container} 返回 {Exit}: {Err}", containerName, exit, err.Trim());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移除沙箱容器 {Container} 失败", containerName);
        }
    }

    /// <summary>容器不存在则创建并启动；已存在则确保其在运行。失败时返回终止结果，成功返回 null。</summary>
    private async Task<SandboxRunResult?> EnsureContainerAsync(
        SandboxRunRequest request,
        IProgress<SandboxLogEvent> progress,
        CancellationToken ct)
    {
        var (_, inspectOut, _) = await RunPodmanAsync(
            ["inspect", "-f", "{{.State.Running}}", request.ContainerName], CancellationToken.None);
        var running = inspectOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!running)
        {
            var exists = !string.IsNullOrWhiteSpace(inspectOut.Trim())
                && !inspectOut.Contains("no such container", StringComparison.OrdinalIgnoreCase);

            if (!exists)
            {
                var runArgs = new List<string>
                {
                    "run", "-d",
                    "--name", request.ContainerName,
                    "--user", SandboxPaths.RootUser,
                    "--label", $"ctf.platform={SandboxPaths.PlatformLabel}",
                    "--network", "host",
                    "--dns", "223.5.5.5",
                    "--dns", "119.29.29.29",
                    "-v", $"{request.WorkspaceHostPath}:{SandboxPaths.Workspace}:rw",
                    "-v", $"{request.WorkspaceHostPath}/.opencode-data:{SandboxPaths.OpencodeData}:rw",
                    "-w", SandboxPaths.Workspace,
                    _options.Image
                };

                progress.Report(new SandboxLogEvent(SandboxLogStream.System,
                    $"[executor] podman {string.Join(' ', runArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}",
                    DateTimeOffset.UtcNow));

                var (runExit, runOut, runErr) = await RunPodmanAsync(runArgs, ct);
                if (runExit != 0)
                {
                    var error = string.IsNullOrWhiteSpace(runErr) ? runOut : runErr;
                    return new SandboxRunResult(false, runExit, null, $"podman run 失败: {error.Trim()}");
                }

                progress.Report(new SandboxLogEvent(SandboxLogStream.System,
                    $"[executor] 常驻容器已创建 {runOut.Trim()[..Math.Min(12, runOut.Trim().Length)]}（镜像 {_options.Image}）",
                    DateTimeOffset.UtcNow));
            }
            else
            {
                var (startExit, _, startErr) = await RunPodmanAsync(["start", request.ContainerName], ct);
                if (startExit != 0)
                {
                    return new SandboxRunResult(false, startExit, null, $"podman start 失败: {startErr.Trim()}");
                }

                progress.Report(new SandboxLogEvent(SandboxLogStream.System,
                    "[executor] 已复用并重启既有容器", DateTimeOffset.UtcNow));
            }
        }
        else
        {
            progress.Report(new SandboxLogEvent(SandboxLogStream.System,
                "[executor] 复用运行中的常驻容器", DateTimeOffset.UtcNow));
        }

        return null;
    }

    private async Task StopRunAsync(string containerName)
    {
        // exec 的 run-challenge.sh 是容器内前台进程；TERM 它即可结束本轮而不杀容器守护循环。
        try
        {
            await RunPodmanAsync(["exec", "--user", SandboxPaths.RootUser, containerName, .. SandboxPaths.StopRunArgs], CancellationToken.None);
        }
        catch
        {
            // container may already be gone
        }
    }

    private async Task PumpLogsAsync(
        Process process,
        IProgress<SandboxLogEvent> progress,
        Action<string> onLine,
        CancellationToken ct)
    {
        var stdoutTask = PumpStreamAsync(process.StandardOutput, SandboxLogStream.Stdout, progress, onLine, ct);
        var stderrTask = PumpStreamAsync(process.StandardError, SandboxLogStream.Stderr, progress, onLine, ct);
        await Task.WhenAll(stdoutTask, stderrTask);
    }

    private static async Task PumpStreamAsync(
        StreamReader reader,
        SandboxLogStream stream,
        IProgress<SandboxLogEvent> progress,
        Action<string> onLine,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                progress.Report(new SandboxLogEvent(SandboxLogStream.System, $"[executor] 日志读取结束: {ex.Message}", DateTimeOffset.UtcNow));
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            onLine(line);
            progress.Report(new SandboxLogEvent(stream, line, DateTimeOffset.UtcNow));
        }
    }

    private Process StartPodman(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "podman",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("无法启动 podman 进程");
    }

    private async Task<(int Exit, string StdOut, string StdErr)> RunPodmanAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        using var process = StartPodman(args);
        var outTask = process.StandardOutput.ReadToEndAsync(ct);
        var errTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await outTask, await errTask);
    }
}
