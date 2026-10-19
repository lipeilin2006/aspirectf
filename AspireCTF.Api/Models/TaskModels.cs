namespace AspireCTF.Api.Models;

public enum ChallengeTaskStatus
{
    Pending,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public sealed class ChallengeTask
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string CreatedBy { get; set; } = "admin";
    public string Category { get; set; } = "web";
    public string Description { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string AdditionalPrompt { get; set; } = "";
    public string Model { get; set; } = "";
    public string ChallengeId { get; set; } = "";
    public bool HasSource { get; set; }
    public bool AllowContinue { get; set; } = true;
    public ChallengeTaskStatus Status { get; set; } = ChallengeTaskStatus.Pending;
    public string? Flag { get; set; }
    public string? Writeup { get; set; }
    public string? ContainerName { get; set; }
    public string? Executor { get; set; }
    public string? Error { get; set; }
    public string? FailureReason { get; set; }
    public string? SessionId { get; set; }
    public int ExitCode { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public ChallengeTaskDto ToDto(long lastSeq = 0, int logCount = 0) => new()
    {
        Id = Id,
        Name = Name,
        CreatedBy = CreatedBy,
        Category = Category,
        Description = Description,
        TargetUrl = TargetUrl,
        Model = Model,
        ChallengeId = ChallengeId,
        HasSource = HasSource,
        Status = Status.ToString(),
        Flag = Flag,
        HasWriteup = !string.IsNullOrWhiteSpace(Writeup),
        ContainerName = ContainerName,
        Executor = Executor,
        Error = Error,
        FailureReason = FailureReason,
        SessionId = SessionId,
        ExitCode = ExitCode,
        CreatedAt = CreatedAt,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        LastSeq = lastSeq,
        LogCount = logCount
    };
}

public sealed class TaskLog
{
    public long Id { get; set; }
    public required string TaskId { get; set; }
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Stream { get; set; }
    public required string Text { get; set; }
    public string? Part { get; set; }
}
