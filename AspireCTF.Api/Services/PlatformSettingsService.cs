using AspireCTF.Api.Models;
using AspireCTF.Api.Runner;
using AspireCTF.Api.Sandbox;
using AspireCTF.Api.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AspireCTF.Api.Services;

public sealed class PlatformSettingsService(
    IDbContextFactory<TaskDbContext> factory,
    IOptionsMonitor<SandboxOptions> options,
    DynamicConcurrencyGate concurrency,
    IDataProtectionProvider protectionProvider,
    ModelsDevCatalogService providers,
    ILogger<PlatformSettingsService> logger)
{
    private readonly object _sync = new();
    private PlatformSettings _current = new();
    private readonly IDataProtector _keyProtector = protectionProvider.CreateProtector("AspireCTF.ApiKeys.v1");
    // 构造时（InitializeAsync/Apply 之前）捕获配置内置默认模板；options.CurrentValue 之后会被 Apply 原地改写，不能作为默认值来源。
    private readonly string _builtinPrompt = options.CurrentValue.PromptTemplate;

    public PlatformSettings Current
    {
        get { lock (_sync) return _current; }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"OpenCodeConfigJson\" text NOT NULL DEFAULT @defaultJson",
            [new Npgsql.NpgsqlParameter("defaultJson", "{}")] , ct);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"ApiKeysProtectedJson\" text NOT NULL DEFAULT @emptyJson",
            [new Npgsql.NpgsqlParameter("emptyJson", "{}")] , ct);
        var saved = await db.Settings.SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (saved is null)
        {
            saved = FromOptions(options.CurrentValue);
            db.Settings.Add(saved);
            await db.SaveChangesAsync(ct);
        }

        Apply(saved);
        logger.LogInformation("Platform settings loaded; API keys configured: {Count}",
            ReadKeys(saved.ApiKeysProtectedJson).Count);
    }

    public async Task<PlatformSettings> UpdateAsync(PlatformSettingsDto dto, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var saved = await db.Settings.SingleOrDefaultAsync(x => x.Id == 1, ct) ?? new PlatformSettings();
        saved.MaxConcurrent = Math.Clamp(dto.MaxConcurrent, 1, 32);
        saved.TaskTimeoutMinutes = Math.Clamp(dto.TaskTimeoutMinutes, 1, 240);
        saved.SandboxImage = string.IsNullOrWhiteSpace(dto.SandboxImage) ? "ctf-opencode-sandbox:latest" : dto.SandboxImage.Trim();
        saved.OpenCodeConfigJson = ValidateJson(dto.OpenCodeConfigJson);
        var keys = ReadKeys(saved.ApiKeysProtectedJson);
        foreach (var pair in dto.ApiKeys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(pair.Value)) keys[pair.Key] = pair.Value.Trim();
        }
        saved.ApiKeysProtectedJson = _keyProtector.Protect(JsonSerializer.Serialize(keys));
        // 与内置默认一致时存空串，后续升级默认模板可自动对所有未自定义的实例生效。
        var prompt = dto.PromptTemplate ?? "";
        saved.PromptTemplate = string.IsNullOrWhiteSpace(prompt) || prompt.Trim() == _builtinPrompt.Trim() ? "" : prompt;
        saved.UpdatedAt = DateTimeOffset.UtcNow;

        if (saved.Id == 1 && db.Entry(saved).State == EntityState.Detached) db.Settings.Add(saved);
        await db.SaveChangesAsync(ct);
        Apply(saved);
        return saved;
    }

    private void Apply(PlatformSettings settings)
    {
        lock (_sync) _current = settings;
        var current = options.CurrentValue;
        current.MaxConcurrent = settings.MaxConcurrent;
        current.TaskTimeoutMinutes = settings.TaskTimeoutMinutes;
        current.Image = settings.SandboxImage;
        current.PromptTemplate = string.IsNullOrWhiteSpace(settings.PromptTemplate) ? _builtinPrompt : settings.PromptTemplate;
        concurrency.SetLimit(settings.MaxConcurrent);
        Environment.SetEnvironmentVariable("OPENCODE_CONFIG_JSON", settings.OpenCodeConfigJson);
        Environment.SetEnvironmentVariable("OPENCODE_CONFIG_CONTENT", settings.OpenCodeConfigJson);
        foreach (var pair in ReadKeys(settings.ApiKeysProtectedJson))
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    }

    private static PlatformSettings FromOptions(SandboxOptions value) => new()
    {
        MaxConcurrent = value.MaxConcurrent,
        TaskTimeoutMinutes = value.TaskTimeoutMinutes,
        SandboxImage = value.Image,
        OpenCodeConfigJson = "{\n  \"$schema\": \"https://opencode.ai/config.json\",\n  \"provider\": {}\n}",
        PromptTemplate = value.PromptTemplate
    };

    public static IReadOnlyList<ConfiguredModelDto> GetModels(string json)
    {
        var models = new List<ConfiguredModelDto>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("provider", out var providers)) return models;
            foreach (var provider in providers.EnumerateObject())
            {
                if (!provider.Value.TryGetProperty("models", out var providerModels)) continue;
                foreach (var model in providerModels.EnumerateObject())
                    models.Add(new ConfiguredModelDto($"{provider.Name}/{model.Name}", $"{provider.Name} / {model.Name}", "用户配置", false, provider.Name));
            }
        }
        catch (JsonException) { }
        return models;
    }

    private static string ValidateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    public PlatformSettingsDto ToDto(PlatformSettings settings)
    {
        var keys = ReadKeys(settings.ApiKeysProtectedJson);
        return new PlatformSettingsDto
        {
            MaxConcurrent = settings.MaxConcurrent,
            TaskTimeoutMinutes = settings.TaskTimeoutMinutes,
            SandboxImage = settings.SandboxImage,
            OpenCodeConfigJson = settings.OpenCodeConfigJson,
            // DB 中为空表示沿用内置默认模板；返回生效值，设置页才能显示可编辑内容。
            PromptTemplate = string.IsNullOrWhiteSpace(settings.PromptTemplate)
                ? _builtinPrompt
                : settings.PromptTemplate,
            ApiKeysConfigured = keys.ToDictionary(x => x.Key, _ => true),
            ApiKeys = keys.Keys.ToDictionary(x => x, _ => (string?)null)
        };
    }

    /// <summary>已配置的环境变量名列表，供沙箱执行器透传。</summary>
    public IReadOnlyCollection<string> ConfiguredEnvVarNames
    {
        get { var keys = ReadKeys(Current.ApiKeysProtectedJson); return keys.Keys.ToArray(); }
    }

    public async Task<IReadOnlyList<ApiKeyStatusDto>> GetApiKeysAsync(CancellationToken ct = default)
    {
        var keys = ReadKeys(Current.ApiKeysProtectedJson);
        var catalog = await providers.GetProvidersAsync(ct);
        var byEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in catalog)
        {
            foreach (var env in p.Env)
            {
                byEnv.TryAdd(env, p.Name);
            }
        }

        IReadOnlyList<ApiKeyStatusDto> result = keys
            .Select(x => new ApiKeyStatusDto(
                x.Key,
                byEnv.TryGetValue(x.Key, out var label) ? label : x.Key,
                true))
            .ToList();
        return result;
    }

    public async Task<bool> SetApiKeyAsync(string provider, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        var envVar = await ResolveEnvVarAsync(provider, ct);
        if (envVar is null)
        {
            logger.LogWarning("SetApiKeyAsync: 无法解析 Provider '{Provider}'", provider);
            return false;
        }
        var keys = ReadKeys(Current.ApiKeysProtectedJson);
        keys[envVar] = apiKey.Trim();
        return await SaveKeysAsync(keys, ct);
    }

    public async Task<bool> RemoveApiKeyAsync(string provider, CancellationToken ct = default)
    {
        var keys = ReadKeys(Current.ApiKeysProtectedJson);
        // 允许用 env 变量名或 Provider id 删除。
        if (keys.Remove(provider)) return await SaveKeysAsync(keys, ct);
        var envVar = await ResolveEnvVarAsync(provider, ct);
        if (envVar is null) return false;
        if (!keys.Remove(envVar)) return false;
        return await SaveKeysAsync(keys, ct);
    }

    /// <summary>
    /// 把用户提交的标识（Provider id 或 env 变量名）解析为要存储的 env 变量名。
    /// Provider 有多个 env 时取第一个含 KEY/TOKEN 的；全部不含则取第一个。
    /// </summary>
    private async Task<string?> ResolveEnvVarAsync(string provider, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var catalog = await providers.GetProvidersAsync(ct);

        // 精确 env 匹配优先（兼容旧客户端直接传 env 名）。
        foreach (var p in catalog)
        {
            if (p.Env.Contains(provider, StringComparer.OrdinalIgnoreCase)) return p.Env.First();
        }

        var match = catalog.FirstOrDefault(x =>
            x.Id.Equals(provider, StringComparison.OrdinalIgnoreCase)
            || x.Name.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (match is null || match.Env.Length == 0)
        {
            logger.LogWarning("ResolveEnvVarAsync: 未匹配到 Provider '{Provider}'（共 {Count} 个）", provider, catalog.Count);
            return null;
        }
        return match.Env.FirstOrDefault(e =>
                   e.Contains("KEY", StringComparison.OrdinalIgnoreCase)
                   || e.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                   || e.Contains("PAT", StringComparison.OrdinalIgnoreCase))
               ?? match.Env[0];
    }

    private async Task<bool> SaveKeysAsync(Dictionary<string, string> keys, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var saved = await db.Settings.SingleAsync(x => x.Id == 1, ct);
        saved.ApiKeysProtectedJson = _keyProtector.Protect(JsonSerializer.Serialize(keys));
        saved.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        Apply(saved);
        return true;
    }

    private Dictionary<string, string> ReadKeys(string? protectedJson)
    {
        if (string.IsNullOrWhiteSpace(protectedJson) || protectedJson == "{}") return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(_keyProtector.Unprotect(protectedJson)) ?? []; }
        catch { return []; }
    }
}
