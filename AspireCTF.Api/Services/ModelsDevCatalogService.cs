using System.Text.Json;
using AspireCTF.Contracts;

namespace AspireCTF.Api.Services;

/// <summary>
/// 运行时从 models.dev（https://models.dev/api.json）拉取 AI Provider 目录。
/// 内存缓存 6 小时 + 磁盘快照兜底（拉取失败时用上次成功的数据）。
/// 目录只含 Provider 元信息（id/name/env/npm/doc/models 数量），不含模型明细。
/// </summary>
public sealed class ModelsDevCatalogService(
    IHttpClientFactory httpFactory,
    ILogger<ModelsDevCatalogService> logger)
{
    private const string CatalogUrl = "https://models.dev/api.json";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    private readonly object _sync = new();
    private readonly string _snapshotPath = Path.Combine(
        AppContext.BaseDirectory, "models-dev-catalog.json");
    private IReadOnlyList<ProviderOptionDto> _providers = [];
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private bool _loadedFromDisk;

    public async Task<IReadOnlyList<ProviderOptionDto>> GetProvidersAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (DateTimeOffset.UtcNow < _expiresAt && _providers.Count > 0) return _providers;
        }

        // 只允许一个刷新任务；其余并发请求直接读内存快照。
        var refreshed = await TryRefreshAsync(ct);
        lock (_sync)
        {
            if (refreshed) return _providers;
            if (!_loadedFromDisk) TryLoadSnapshot();
            return _providers;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _expiresAt = DateTimeOffset.MinValue;
        }
    }

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("modelsdev");
            using var response = await http.GetAsync(CatalogUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var providers = await ParseAsync(stream, ct);
            if (providers.Count == 0) return false;

            lock (_sync)
            {
                _providers = providers;
                _expiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
                _loadedFromDisk = false;
            }

            _ = SaveSnapshotAsync(providers, CancellationToken.None);
            logger.LogInformation("models.dev 目录已刷新：{Count} 个 Provider", providers.Count);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "拉取 models.dev 目录失败，使用缓存数据（{Count} 个 Provider）",
                _providers.Count);
            return false;
        }
    }

    private static async Task<List<ProviderOptionDto>> ParseAsync(Stream stream, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<ProviderOptionDto>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var value = prop.Value;
            var name = value.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var env = ParseEnv(value);
            var docUrl = value.TryGetProperty("doc", out var docEl) ? docEl.GetString() ?? "" : "";
            var modelCount = value.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Object
                ? modelsEl.EnumerateObject().Count()
                : 0;

            list.Add(new ProviderOptionDto(prop.Name, name, env, docUrl, modelCount));
        }

        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string[] ParseEnv(JsonElement provider)
    {
        if (!provider.TryGetProperty("env", out var env)) return [];
        if (env.ValueKind == JsonValueKind.String)
        {
            var single = env.GetString();
            return string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
        }

        if (env.ValueKind == JsonValueKind.Array)
        {
            return env.EnumerateArray()
                .Select(x => x.GetString()?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToArray();
        }

        return [];
    }

    private void TryLoadSnapshot()
    {
        try
        {
            if (!File.Exists(_snapshotPath)) return;
            var providers = JsonSerializer.Deserialize<List<ProviderOptionDto>>(
                File.ReadAllText(_snapshotPath));
            if (providers is { Count: > 0 })
            {
                _providers = providers;
                // 磁盘快照视为已过期：下次请求仍会尝试在线刷新。
                _expiresAt = DateTimeOffset.MinValue;
                _loadedFromDisk = true;
                logger.LogInformation("从磁盘快照加载 models.dev 目录：{Count} 个 Provider", providers.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "读取 models.dev 磁盘快照失败");
        }
    }

    private async Task SaveSnapshotAsync(IReadOnlyList<ProviderOptionDto> providers, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(providers);
            await File.WriteAllTextAsync(_snapshotPath, json, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "写入 models.dev 磁盘快照失败");
        }
    }
}
