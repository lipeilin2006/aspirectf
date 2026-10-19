using System.IO.Compression;
using AspireCTF.Api.Models;
using Microsoft.Extensions.Options;
using AspireCTF.Api.Sandbox;

namespace AspireCTF.Api.Services;

public sealed class WorkspaceService
{
    private readonly SandboxOptions _options;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(IOptions<SandboxOptions> options, ILogger<WorkspaceService> logger)
    {
        _options = options.Value;
        _logger = logger;
        Directory.CreateDirectory(_options.WorkspacesRoot);
    }

    public string Root => _options.WorkspacesRoot;

    public async Task<string> PrepareAsync(ChallengeTask task, Stream? zipStream, string? catalogSourcePath, CancellationToken ct)
    {
        var dir = Path.Combine(_options.WorkspacesRoot, task.Id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".opencode-data"));

        if (!string.IsNullOrWhiteSpace(catalogSourcePath) && Directory.Exists(catalogSourcePath))
        {
            CopyDirectory(catalogSourcePath, dir);
        }

        if (zipStream is not null)
        {
            await using var ms = new MemoryStream();
            await zipStream.CopyToAsync(ms, ct);
            ms.Position = 0;
            ExtractZipSafely(ms, dir);
        }

        var meta = $"""
            # Task {task.Id}
            Name: {task.Name}
            Category: {task.Category}
            Target: {task.TargetUrl}
            Created: {task.CreatedAt:O}
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "TASK.md"), meta, ct);

        _logger.LogInformation("任务 {TaskId} 工作目录已准备: {Dir}", task.Id, dir);
        return dir;
    }

    public void Cleanup(string taskId)
    {
        var dir = Path.Combine(_options.WorkspacesRoot, taskId);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理工作目录失败 {Dir}", dir);
        }
    }

    private static void ExtractZipSafely(Stream zipStream, string targetDir)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var fullRoot = Path.GetFullPath(targetDir + Path.DirectorySeparatorChar);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!dest.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"zip 包含非法路径（zip-slip）: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
    }
}
