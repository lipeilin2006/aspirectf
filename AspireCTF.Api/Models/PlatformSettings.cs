namespace AspireCTF.Api.Models;

public sealed class PlatformSettings
{
    public int Id { get; set; } = 1;
    public int MaxConcurrent { get; set; } = 3;
    public int TaskTimeoutMinutes { get; set; } = 20;
    public string SandboxImage { get; set; } = "ctf-opencode-sandbox:latest";
    public string OpenCodeConfigJson { get; set; } = "{}";
    public string ApiKeysProtectedJson { get; set; } = "{}";
    public string PromptTemplate { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
