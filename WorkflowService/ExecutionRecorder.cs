using System.Collections.Concurrent;
using System.Diagnostics;
using Dapr.Workflow;

namespace ParallelRerunRepro;

// Evidence is produced inside real worker executions, never inside replayed workflow code.
public sealed class ExecutionRecorder
{
    public const string SourceName = "ParallelRerunRepro";
    public static readonly ActivitySource Source = new(SourceName);
    private readonly ConcurrentDictionary<string, ExecutionEntry> _entries = new();
    private readonly ILogger<ExecutionRecorder> _logger;

    public ExecutionRecorder(ILogger<ExecutionRecorder> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ExecutionEntry> Read(string? instanceId = null)
    {
        return _entries.Values
            .Where(entry => instanceId is null || entry.InstanceId == instanceId)
            .OrderBy(entry => entry.StartedAt)
            .ToArray();
    }

    public async Task<ActivityResult> ExecuteAsync(
        WorkflowActivityContext context, string activityName, int delayMilliseconds = 0, bool fail = false)
    {
        var invocationId = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        using var span = Source.StartActivity($"Execute {activityName}", ActivityKind.Internal);
        span?.SetTag("workflow.instance.id", context.InstanceId);
        span?.SetTag("workflow.activity.name", activityName);
        span?.SetTag("workflow.scheduled_event.id", context.ScheduledEventId);
        span?.SetTag("demo.invocation.id", invocationId);
        var entry = new ExecutionEntry(context.InstanceId, activityName, invocationId,
            context.ScheduledEventId, started, null, "Running", null, Activity.Current?.TraceId.ToString());
        _entries[invocationId] = entry;
        _logger.LogInformation(
            "ACTIVITY_EXECUTED instance={InstanceId} activity={ActivityName} schedule={ScheduledEventId} invocation={InvocationId}",
            context.InstanceId, activityName, context.ScheduledEventId, invocationId);

        try
        {
            await Task.Delay(delayMilliseconds, context.CancellationToken);
            if (fail)
            {
                throw new InvalidOperationException("Intentional source Save failure. Rerun from Inventory next.");
            }

            var completed = DateTimeOffset.UtcNow;
            _entries[invocationId] = entry with { CompletedAt = completed, Status = "Completed" };
            _logger.LogInformation("ACTIVITY_COMPLETED instance={InstanceId} activity={ActivityName} invocation={InvocationId}",
                context.InstanceId, activityName, invocationId);

            return new ActivityResult(activityName, invocationId, context.InstanceId,
                context.ScheduledEventId, started, completed);
        }
        catch (Exception exception)
        {
            _entries[invocationId] = entry with
            {
                CompletedAt = DateTimeOffset.UtcNow, Status = "Failed", Error = exception.Message,
            };
            span?.SetStatus(ActivityStatusCode.Error, exception.Message);
            _logger.LogWarning(exception, "ACTIVITY_FAILED instance={InstanceId} activity={ActivityName} invocation={InvocationId}",
                context.InstanceId, activityName, invocationId);

            throw;
        }
    }
}
