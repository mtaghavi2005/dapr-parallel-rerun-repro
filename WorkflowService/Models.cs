namespace ParallelRerunRepro;

public sealed record StartRequest(int DelaySeconds = 5);
public sealed record RerunRequest
{
    public required uint EventId { get; init; }
}

public sealed record OrderInput(string OrderId, string SourceInstanceId, int DelaySeconds);

public sealed record ActivityResult(
    string ActivityName,
    string InvocationId,
    string ExecutedByInstanceId,
    int ScheduledEventId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record WorkflowOutput(
    ActivityResult Load,
    ActivityResult Validate,
    ActivityResult Inventory,
    ActivityResult Charge,
    ActivityResult Save);

public sealed record ExecutionEntry(
    string InstanceId,
    string ActivityName,
    string InvocationId,
    int ScheduledEventId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? Error,
    string? TraceId);

public sealed record HistoryEntry(
    int Position,
    int EventId,
    string EventType,
    DateTime? Timestamp,
    int? ScheduledEventId,
    string? ActivityName,
    string? TaskExecutionId,
    System.Text.Json.JsonElement? Output);
