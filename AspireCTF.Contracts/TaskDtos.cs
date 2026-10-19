namespace AspireCTF.Contracts;

public sealed record ChallengeTaskDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string CreatedBy { get; init; } = "admin";
    public string Category { get; init; } = "web";
    public string Description { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public string ChallengeId { get; init; } = "";
    public bool HasSource { get; init; }
    public bool AllowContinue { get; init; }
    public required string Status { get; init; }
    public string? Flag { get; init; }
    public bool HasWriteup { get; init; }
    public string? ContainerName { get; init; }
    public string? Executor { get; init; }
    public string? Error { get; init; }
    public string? FailureReason { get; init; }
    public string? SessionId { get; init; }
    public int ExitCode { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long LastSeq { get; init; }
    public int LogCount { get; init; }
}

public sealed record LogLineDto(long Seq, string At, string Stream, string Text, string? Part = null);

public sealed record SubmitTaskRequest
{
    public string Name { get; set; } = "";
    public string CreatedBy { get; set; } = "admin";
    public string Category { get; set; } = "web";
    public string Description { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "";
    public string ChallengeId { get; set; } = "";
}

public sealed record ContinueTaskRequest(string AdditionalPrompt);

public sealed record ChallengeCatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "web";
    public string Description { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public string? SourcePath { get; set; }
    public string? DefaultPrompt { get; set; }
}

public sealed record SystemInfoDto
{
    public required string Executor { get; init; }
    public required string ExecutorAvailable { get; init; }
    public required string[] AvailableExecutors { get; init; }
    public int MaxConcurrent { get; init; }
    public int Running { get; init; }
    public int QueueLength { get; init; }
    public int TotalTasks { get; init; }
    public bool PostgresConnected { get; init; }
    public string SandboxImage { get; init; } = "";
    public bool SandboxReady { get; init; }
    public string SandboxStatus { get; init; } = "Unknown";
    public string SandboxMessage { get; init; } = "";
    public string[] SandboxBuildOutput { get; init; } = [];
}

/// <summary>任务状态 → 展示文案（看板卡片与详情页共用）。</summary>
public static class TaskStatusText
{
    public static string From(string status) => status switch
    {
        "Pending" => "待处理",
        "Queued" => "排队中",
        "Running" => "解题中",
        "Succeeded" => "已获取 Flag",
        "Failed" => "失败",
        "Cancelled" => "已取消",
        "TimedOut" => "超时",
        _ => status
    };
}
