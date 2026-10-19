namespace AspireCTF.Api.Sandbox;

public enum SandboxLogStream
{
    Stdout,
    Stderr,
    System
}

public sealed record SandboxLogEvent(SandboxLogStream Stream, string Text, DateTimeOffset At, string? Part = null);

public sealed record SandboxRunRequest(
    string ContainerName,
    string WorkspaceHostPath,
    string Prompt,
    IReadOnlyDictionary<string, string> Env,
    TimeSpan Timeout);

public sealed record SandboxRunResult(
    bool Success,
    int ExitCode,
    string? Flag,
    string? Error,
    string? SessionId = null,
    string? Writeup = null);

public interface ISandboxExecutor
{
    string Name { get; }

    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<SandboxRunResult> RunAsync(
        SandboxRunRequest request,
        IProgress<SandboxLogEvent> progress,
        CancellationToken ct);

    /// <summary>任务被删除时调用，强制移除其常驻沙箱容器（运行中会先终止）。</summary>
    Task RemoveContainerAsync(string? containerName, CancellationToken ct = default);
}
