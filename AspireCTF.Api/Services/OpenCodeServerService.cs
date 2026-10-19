using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AspireCTF.Api.Models;

namespace AspireCTF.Api.Services;

/// <summary>
/// 通过 OpenCode 官方 HTTP API（<c>opencode serve</c>）获取模型列表与版本信息。
/// 平台内所有 OpenCode 能力一律经 HTTP API 调用，不再直接执行 opencode CLI 子命令。
/// </summary>
public sealed class OpenCodeServerService(
    ModelsDevCatalogService providerCatalog,
    ILogger<OpenCodeServerService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OpenCodeApiResult> GetModelsAsync(CancellationToken ct = default)
    {
        try
        {
            return await WithServerAsync(async (client, baseUrl) =>
            {
                using var response = await client.GetAsync($"{baseUrl}/api/model", ct);
                if (!response.IsSuccessStatusCode)
                {
                    return new OpenCodeApiResult(false, [], $"GET /api/model HTTP {(int)response.StatusCode}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var doc = await JsonSerializer.DeserializeAsync<JsonElement>(stream, Json, ct);
                if (!doc.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    return new OpenCodeApiResult(false, [], "GET /api/model 未返回 data 数组");
                }

                var models = new List<ConfiguredModelDto>();

                // 构建 providerID → 友好名 映射（models.dev 目录）。
                Dictionary<string, string> providerNames = new(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var providers = await providerCatalog.GetProvidersAsync(ct);
                    foreach (var p in providers) providerNames[p.Id] = p.Name;
                }
                catch { /* 目录不可用时用 providerID 兜底 */ }

                foreach (var item in data.EnumerateArray())
                {
                    var provider = item.TryGetProperty("providerID", out var providerId)
                        ? providerId.GetString() ?? ""
                        : "";
                    var modelId = item.TryGetProperty("modelID", out var modelRef)
                        ? modelRef.GetString() ?? ""
                        : "";
                    if (provider.Length == 0 || modelId.Length == 0) continue;

                    var id = $"{provider}/{modelId}";
                    var label = item.TryGetProperty("name", out var name)
                        ? name.GetString() ?? id
                        : id;
                    var isFree = provider.Equals("opencode", StringComparison.OrdinalIgnoreCase)
                                 || id.EndsWith("-free", StringComparison.OrdinalIgnoreCase);
                    var providerLabel = providerNames.TryGetValue(provider, out var pn) ? pn : provider;
                    models.Add(new ConfiguredModelDto(id, label, "OpenCode API", isFree, providerLabel));
                }

                return new OpenCodeApiResult(true, models, "");
            }, ct);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException
                                       or HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "OpenCode HTTP API 模型发现失败");
            return new OpenCodeApiResult(false, [], ex.Message);
        }
    }

    public async Task<OpenCodeApiValidation> ValidateAsync(CancellationToken ct = default)
    {
        try
        {
            return await WithServerAsync(async (client, baseUrl) =>
            {
                using var response = await client.GetAsync($"{baseUrl}/api/info", ct);
                if (!response.IsSuccessStatusCode)
                {
                    return new OpenCodeApiValidation(false, "", $"GET /api/info HTTP {(int)response.StatusCode}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var doc = await JsonSerializer.DeserializeAsync<JsonElement>(stream, Json, ct);
                var version = doc.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                return new OpenCodeApiValidation(version.Length > 0, version, "");
            }, ct);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException
                                       or HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "OpenCode HTTP API 校验失败");
            return new OpenCodeApiValidation(false, "", ex.Message);
        }
    }

    private async Task<T> WithServerAsync<T>(Func<HttpClient, string, Task<T>> action, CancellationToken ct)
    {
        var port = ReserveLoopbackPort();
        var password = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

        var startInfo = new ProcessStartInfo
        {
            FileName = "opencode",
            WorkingDirectory = "/app",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--hostname");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.Environment["OPENCODE_SERVER_PASSWORD"] = password;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 opencode serve");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            var baseUrl = $"http://127.0.0.1:{port}";
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    var stderr = stderrTask.IsCompleted ? await stderrTask : "";
                    throw new InvalidOperationException($"opencode serve 已退出（exit={process.ExitCode}）: {stderr.Trim()}");
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new InvalidOperationException("opencode serve 在 20 秒内未就绪");
                }

                try
                {
                    using var probe = await http.GetAsync($"{baseUrl}/api/info", ct);
                    if (probe.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException)
                {
                    // 尚未监听，继续等待
                }

                await Task.Delay(250, ct);
            }

            return await action(http, baseUrl);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "停止临时 opencode serve 进程失败");
            }

            _ = stdoutTask;
            _ = stderrTask;
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

public sealed record OpenCodeApiResult(bool Available, IReadOnlyList<ConfiguredModelDto> Models, string Error);
public sealed record OpenCodeApiValidation(bool Available, string Version, string Error);
