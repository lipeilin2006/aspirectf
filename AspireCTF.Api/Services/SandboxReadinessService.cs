using System.Diagnostics;
using AspireCTF.Api.Sandbox;
using Microsoft.Extensions.Options;

namespace AspireCTF.Api.Services;

public sealed class SandboxReadinessService(IOptions<SandboxOptions> options)
{
    public SandboxReadiness GetStatus()
    {
        var image = options.Value.Image;
        const string statusFile = "/var/log/ctf-sandbox-build.status";
        const string buildLog = "/var/log/ctf-sandbox-build.log";
        try
        {
            var start = new ProcessStartInfo("podman", ["image", "exists", image])
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            process!.WaitForExit(3000);
            if (process.ExitCode == 0)
            {
                return new(true, "Ready", image, "沙箱镜像已就绪", []);
            }
        }
        catch { }

        if (File.Exists(statusFile))
        {
            var status = File.ReadAllText(statusFile).Trim();
            var output = File.Exists(buildLog) ? File.ReadLines(buildLog).TakeLast(12).ToArray() : [];
            if (status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                return new(false, "Failed", image, "沙箱镜像构建失败", output);
            if (status.Equals("building", StringComparison.OrdinalIgnoreCase))
                return new(false, "Building", image, "沙箱镜像正在构建", output);
        }

        if (File.Exists(buildLog))
        {
            var tail = File.ReadLines(buildLog).TakeLast(12).ToArray();
            if (tail.Any(x => x.Contains("WARN:", StringComparison.OrdinalIgnoreCase) || x.Contains("error", StringComparison.OrdinalIgnoreCase)))
            {
                return new(false, "Failed", image, string.Join(Environment.NewLine, tail), tail);
            }

            return new(false, "Building", image, "沙箱镜像正在构建", tail);
        }

        return new(false, "Building", image, "沙箱镜像正在构建，请等待完成", []);
    }
}

public sealed record SandboxReadiness(bool Ready, string Status, string Image, string Message, IReadOnlyList<string> Output = null!);
