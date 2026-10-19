namespace AspireCTF.Contracts;

public sealed record PlatformSettingsDto
{
    public int MaxConcurrent { get; set; } = 3;
    public int TaskTimeoutMinutes { get; set; } = 20;
    public string SandboxImage { get; set; } = "";
    public string OpenCodeConfigJson { get; set; } = "{}";
    public string PromptTemplate { get; set; } = "";
    public Dictionary<string, bool> ApiKeysConfigured { get; set; } = [];
    public Dictionary<string, string?> ApiKeys { get; set; } = [];
}

public sealed record ApiKeyStatusDto(string Provider, string Label, bool Configured);

public sealed record ApiKeyUpdateRequest(string Provider, string ApiKey);

public sealed record ConfiguredModelDto(string Id, string Label, string Source, bool IsFree = false, string ProviderLabel = "");

/// <summary>models.dev 上的 AI Provider（运行时拉取，不写死）。</summary>
public sealed record ProviderOptionDto(string Id, string Name, string[] Env, string Doc, int ModelCount);
