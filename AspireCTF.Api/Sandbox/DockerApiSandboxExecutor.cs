using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace AspireCTF.Api.Sandbox;

/// <summary>
/// 通过 Docker 兼容 REST API 驱动沙箱容器。容器化部署时连接的是 API 容器内部的
/// podman system service（podman-in-podman）；本地开发时自动探测 Docker/Podman 命名管道。
/// 容器常驻：创建一次后每轮运行只 exec 进同一容器，会话与工作区随容器保留；
/// 仅在任务被删除时才移除容器。
/// </summary>
public sealed class DockerApiSandboxExecutor : ISandboxExecutor, IDisposable
{
    private readonly SandboxOptions _options;
    private readonly ILogger<DockerApiSandboxExecutor> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private DockerClient? _client;
    private string? _resolvedEndpoint;

    public DockerApiSandboxExecutor(IOptions<SandboxOptions> options, ILogger<DockerApiSandboxExecutor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "podman-api";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        if (client is null)
        {
            return false;
        }

        try
        {
            await client.System.GetSystemInfoAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker/Podman API 探测失败: {Endpoint}", _resolvedEndpoint);
            return false;
        }
    }

    public async Task<SandboxRunResult> RunAsync(
        SandboxRunRequest request,
        IProgress<SandboxLogEvent> progress,
        CancellationToken ct)
    {
        var client = await GetClientAsync(ct)
            ?? throw new InvalidOperationException("没有可用的 Docker/Podman API 端点");

        var containerId = await EnsureContainerAsync(client, request, progress, ct);
        if (containerId is null)
        {
            return new SandboxRunResult(false, -1, null, "常驻沙箱容器创建失败");
        }

        var execResponse = await client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            // opencode 与任务脚本必须以 root 运行，Agent 才能按需 apt/pip 装工具。
            User = SandboxPaths.RootUid,
            Cmd = [SandboxPaths.RunScript],
            WorkingDir = SandboxPaths.Workspace,
            Env = request.Env.Select(kv => $"{kv.Key}={kv.Value}").ToList()
        }, ct);

        var execId = execResponse.ID;
        progress.Report(new SandboxLogEvent(SandboxLogStream.System,
            $"[executor] 已 exec 进入常驻容器 {request.ContainerName}（exec {execId[..Math.Min(12, execId.Length)]}）",
            DateTimeOffset.UtcNow));

        var capture = new FlagCapture(progress);
        var exitCode = -1;
        var timedOut = false;

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(request.Timeout);

        try
        {
            using var stream = await client.Exec.StartAndAttachContainerExecAsync(execId, false, runCts.Token);
            // 捕获 Flag 仅记录，不提前终止：任务由 OpenCode 自然结束
            // （收尾三件套 flag.txt / failure.txt / writeup.md 见 AGENTS.md）。
            await StreamExecOutputAsync(stream, progress, line => { _ = capture.TryCapture(line); }, runCts.Token);
        }
        catch (OperationCanceledException)
        {
            // handled below via runCts state
        }

        if (runCts.IsCancellationRequested)
        {
            // Timeout / user cancel: terminate the in-container
            // run-challenge.sh so the exec actually ends, then read its exit code.
            timedOut = !ct.IsCancellationRequested && !capture.Captured;
            await StopRunAsync(client, containerId);
            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        }

        try
        {
            var inspect = await client.Exec.InspectContainerExecAsync(execId, CancellationToken.None);
            exitCode = (int)inspect.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

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
            var client = await GetClientAsync(ct);
            if (client is null)
            {
                return;
            }

            await client.Containers.RemoveContainerAsync(containerName, new ContainerRemoveParameters { Force = true }, ct);
            _logger.LogInformation("沙箱容器已移除: {Container}", containerName);
        }
        catch (DockerContainerNotFoundException)
        {
            // already gone
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移除沙箱容器 {Container} 失败", containerName);
        }
    }

    /// <summary>容器不存在则创建并启动；已存在则确保其在运行。返回容器 id。</summary>
    private async Task<string?> EnsureContainerAsync(
        DockerClient client,
        SandboxRunRequest request,
        IProgress<SandboxLogEvent> progress,
        CancellationToken ct)
    {
        ContainerInspectResponse? inspect = null;
        try
        {
            inspect = await client.Containers.InspectContainerAsync(request.ContainerName, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            // fall through to create
        }

        if (inspect is null)
        {
            var createResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = request.ContainerName,
                Image = _options.Image,
                Tty = false,
                OpenStdin = false,
                User = SandboxPaths.RootUid,
                WorkingDir = SandboxPaths.Workspace,
                Labels = new Dictionary<string, string>
                {
                    ["ctf.platform"] = "aspire-ctf",
                    ["ctf.container"] = request.ContainerName
                },
                HostConfig = new HostConfig
                {
                    Binds = [$"{request.WorkspaceHostPath}:{SandboxPaths.Workspace}:rw", $"{request.WorkspaceHostPath}/.opencode-data:{SandboxPaths.OpencodeData}:rw"],
                    AutoRemove = false,
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No },
                    // Nested Podman may not have a working netavark/slirp DNS setup.
                    // Reuse the privileged API container network so OpenCode can reach
                    // the inference endpoint from the sandbox.
                    NetworkMode = "host"
                }
            }, ct);

            await client.Containers.StartContainerAsync(createResponse.ID, new ContainerStartParameters(), ct);
            progress.Report(new SandboxLogEvent(SandboxLogStream.System,
                $"[executor] 常驻容器已创建 {createResponse.ID[..12]}（镜像 {_options.Image}，引擎 {_resolvedEndpoint}）",
                DateTimeOffset.UtcNow));
            return createResponse.ID;
        }

        if (inspect.State is { Running: false })
        {
            await client.Containers.StartContainerAsync(inspect.ID, new ContainerStartParameters(), ct);
            progress.Report(new SandboxLogEvent(SandboxLogStream.System,
                "[executor] 已复用并重启既有容器", DateTimeOffset.UtcNow));
        }
        else
        {
            progress.Report(new SandboxLogEvent(SandboxLogStream.System,
                "[executor] 复用运行中的常驻容器", DateTimeOffset.UtcNow));
        }

        return inspect.ID;
    }

    private async Task StopRunAsync(DockerClient client, string containerId)
    {
        try
        {
            var exec = await client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
            {
                AttachStdout = false,
                AttachStderr = false,
                User = SandboxPaths.RootUid,
                Cmd = SandboxPaths.StopRunArgs
            }, CancellationToken.None);
            using var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, CancellationToken.None);
        }
        catch
        {
            // container may already be gone
        }
    }

    private static async Task StreamExecOutputAsync(
        MultiplexedStream stream,
        IProgress<SandboxLogEvent> progress,
        Action<string> onLine,
        CancellationToken ct)
    {
        var buffer = new byte[8192];
        var pending = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            MultiplexedStream.ReadResult result;
            try
            {
                result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                progress.Report(new SandboxLogEvent(SandboxLogStream.System, $"[executor] 日志流读取结束: {ex.Message}", DateTimeOffset.UtcNow));
                break;
            }

            if (result.EOF)
            {
                break;
            }

            if (result.Count == 0)
            {
                continue;
            }

            pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            var isStderr = result.Target == MultiplexedStream.TargetStream.StandardError;
            while (true)
            {
                var text = pending.ToString();
                var idx = text.IndexOf('\n');
                if (idx < 0)
                {
                    break;
                }

                var line = text[..idx].TrimEnd('\r');
                pending.Remove(0, idx + 1);
                if (line.Length == 0)
                {
                    continue;
                }

                onLine(line);
                progress.Report(new SandboxLogEvent(
                    isStderr ? SandboxLogStream.Stderr : SandboxLogStream.Stdout,
                    line, DateTimeOffset.UtcNow));
            }
        }

        if (pending.Length > 0)
        {
            var line = pending.ToString().TrimEnd('\r');
            onLine(line);
            progress.Report(new SandboxLogEvent(SandboxLogStream.Stdout, line, DateTimeOffset.UtcNow));
        }
    }

    private async Task<DockerClient?> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            foreach (var candidate in CandidateEndpoints())
            {
                try
                {
                    var config = new DockerClientConfiguration(candidate.Uri);
                    var probeClient = config.CreateClient();
                    await probeClient.System.GetSystemInfoAsync(ct);
                    probeClient.Dispose();
                    _client = config.CreateClient();
                    _resolvedEndpoint = candidate.Description;
                    _logger.LogInformation("沙箱引擎已连接: {Endpoint} ({Kind})", candidate.Description, candidate.Kind);
                    return _client;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "候选引擎不可用: {Endpoint}", candidate.Description);
                }
            }

            return null;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private IEnumerable<(Uri Uri, string Description, string Kind)> CandidateEndpoints()
    {
        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            yield return (new Uri(_options.Endpoint), _options.Endpoint, "configured");
            yield break;
        }

        if (OperatingSystem.IsLinux() && File.Exists("/run/podman/podman.sock"))
        {
            yield return (new Uri("unix:///run/podman/podman.sock"), "unix:///run/podman/podman.sock (in-container podman)", "podman-in-podman");
        }

        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            yield return (new Uri(dockerHost), dockerHost, "docker-host");
        }

        if (OperatingSystem.IsWindows())
        {
            yield return (new Uri("npipe://./pipe/docker_engine"), "npipe docker_engine", "docker-compat");
            yield return (new Uri("npipe://./pipe/podman-machine-default"), "npipe podman-machine-default", "podman-machine");
        }
        else if (File.Exists("/var/run/docker.sock"))
        {
            yield return (new Uri("unix:///var/run/docker.sock"), "unix:///var/run/docker.sock", "docker");
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _initLock.Dispose();
    }
}
