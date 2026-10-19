using AspireCTF.Api.Services;

namespace AspireCTF.Api.Sandbox;

/// <summary>沙箱容器内固定路径 / 镜像内命令（Docker API 与 podman CLI 执行器共用）。</summary>
internal static class SandboxPaths
{
    public const string Workspace = "/workspace";
    public const string OpencodeData = "/root/.local/share/opencode";
    public const string RunScript = "/opt/sandbox/run-challenge.sh";
    public const string PlatformLabel = "aspire-ctf";
    /// <summary>Docker API exec/create 使用 uid "0"；podman CLI 使用用户名 "root"。</summary>
    public const string RootUid = "0";
    public const string RootUser = "root";
    public static readonly string[] StopRunArgs = ["pkill", "-TERM", "-f", "run-challenge.sh"];
}

/// <summary>读取宿主机 workspace 挂载目录下的结果文件（flag / sessionId / failure）。</summary>
internal static class WorkspaceFiles
{
    public static string? TryRead(string workspaceHostPath, string fileName, bool extractFlag = false)
    {
        try
        {
            var path = Path.Combine(workspaceHostPath, fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var content = File.ReadAllText(path).Trim();
            return content.Length == 0 ? null
                : extractFlag ? FlagExtractor.Extract(content) ?? content : content;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>流式日志中一次性捕获 Flag（首个匹配生效）。</summary>
internal sealed class FlagCapture(IProgress<SandboxLogEvent> progress)
{
    public string? Flag { get; private set; }
    public bool Captured => Flag is not null;

    public bool TryCapture(string line)
    {
        if (Captured)
        {
            return false;
        }

        var flag = FlagExtractor.Extract(line);
        if (flag is null)
        {
            return false;
        }

        Flag = flag;
        progress.Report(new SandboxLogEvent(SandboxLogStream.System, $"[executor] 日志中捕获 Flag: {flag}", DateTimeOffset.UtcNow));
        return true;
    }
}

/// <summary>执行结束后的统一收尾：日志捕获的 Flag 优先，其次读 workspace 结果文件，按 取消/超时/成功/失败 生成结果。</summary>
internal static class SandboxRunOutcome
{
    public static SandboxRunResult Finalize(
        SandboxRunRequest request,
        int exitCode,
        string? flagFromLogs,
        bool timedOut,
        bool cancelled)
    {
        var flag = flagFromLogs ?? WorkspaceFiles.TryRead(request.WorkspaceHostPath, "flag.txt", extractFlag: true);
        var sessionId = WorkspaceFiles.TryRead(request.WorkspaceHostPath, ".opencode-session-id");
        var writeup = WorkspaceFiles.TryRead(request.WorkspaceHostPath, "writeup.md");
        var failureReason = flag is null
            ? WorkspaceFiles.TryRead(request.WorkspaceHostPath, "failure.txt")
                ?? WorkspaceFiles.TryRead(request.WorkspaceHostPath, "flag.txt.failure")
            : null;

        if (cancelled)
        {
            return new SandboxRunResult(false, exitCode, flag, "任务被取消", sessionId, writeup);
        }

        if (timedOut && flag is null)
        {
            return new SandboxRunResult(false, exitCode, null,
                failureReason ?? $"超过 {request.Timeout.TotalMinutes:F0} 分钟未完成，已强制超时", sessionId, writeup);
        }

        return flag is not null
            ? new SandboxRunResult(true, exitCode, flag, null, sessionId, writeup)
            : new SandboxRunResult(false, exitCode, null,
                failureReason ?? $"沙箱退出（exit={exitCode}），未在输出或 flag.txt 中捕获到 Flag", sessionId, writeup);
    }
}
