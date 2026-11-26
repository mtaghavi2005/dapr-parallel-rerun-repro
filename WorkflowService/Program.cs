using System.Text.Json;
using Dapr;
using Dapr.DurableTask.Protobuf;
using Dapr.Workflow;
using Dapr.Workflow.Client;
using Grpc.Core;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ParallelRerunRepro;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ExecutionRecorder>();
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterWorkflow<ParallelOrderWorkflow>();
    options.RegisterActivity<RerunLoadRequestActivity>();
    options.RegisterActivity<RerunValidateActivity>();
    options.RegisterActivity<RerunInventoryActivity>();
    options.RegisterActivity<RerunChargeActivity>();
    options.RegisterActivity<RerunSaveActivity>();
});

builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
    {
        options.AddOtlpExporter();
    }
});
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.SetSampler(new AlwaysOnSampler())
            .AddSource("Dapr.Workflow", ExecutionRecorder.SourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
        {
            metrics.AddOtlpExporter();
        }
    });

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

app.MapPost("/api/runs", async (StartRequest request, DaprWorkflowClient client) =>
{
    if (request.DelaySeconds is < 2 or > 30)
    {
        return Results.BadRequest("delaySeconds must be between 2 and 30.");
    }

    var sourceId = $"source-{Guid.NewGuid():N}";
    var input = new OrderInput($"order-{Guid.NewGuid():N}", sourceId, request.DelaySeconds);
    await client.ScheduleNewWorkflowAsync(nameof(ParallelOrderWorkflow), sourceId, input);

    return Results.Ok(new { sourceInstanceId = sourceId });
});

app.MapGet("/api/runs/{instanceId}", async (string instanceId, DaprWorkflowClient client) =>
{
    var state = await client.GetWorkflowStateAsync(instanceId);

    return state.Exists
        ? Results.Ok(new
        {
            instanceId,
            status = state.RuntimeStatus.ToString(),
            output = state.RuntimeStatus == WorkflowRuntimeStatus.Completed ? state.ReadOutputAs<WorkflowOutput>() : null,
        })
        : Results.NotFound();
});

app.MapGet("/api/executions", (string? instanceId, ExecutionRecorder recorder) => recorder.Read(instanceId));

app.MapGet("/api/runs/{instanceId}/history", async (
    string instanceId, TaskHubSidecarService.TaskHubSidecarServiceClient client,
    IConfiguration configuration, CancellationToken cancellationToken) =>
    Results.Ok(await ReadHistory(instanceId, client, configuration, cancellationToken)));

app.MapPost("/api/runs/{sourceId}/rerun-inventory", (
    string sourceId, DaprWorkflowClient workflows,
    TaskHubSidecarService.TaskHubSidecarServiceClient historyClient,
    IConfiguration configuration, CancellationToken cancellationToken) =>
    Rerun(sourceId, null, workflows, historyClient, configuration, cancellationToken));

app.MapPost("/api/runs/{sourceId}/rerun", (
    string sourceId, RerunRequest request, DaprWorkflowClient workflows,
    TaskHubSidecarService.TaskHubSidecarServiceClient historyClient,
    IConfiguration configuration, CancellationToken cancellationToken) =>
    Rerun(sourceId, request.EventId, workflows, historyClient, configuration, cancellationToken));

app.MapGet("/api/compare", async (
    string source, string rerun, DaprWorkflowClient workflows, ExecutionRecorder recorder,
    TaskHubSidecarService.TaskHubSidecarServiceClient historyClient,
    IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var state = await workflows.GetWorkflowStateAsync(rerun, cancellation: cancellationToken);
    if (state.RuntimeStatus != WorkflowRuntimeStatus.Completed)
    {
        return Results.Conflict("Wait until the rerun is completed before comparing results.");
    }

    var output = state.ReadOutputAs<WorkflowOutput>()
        ?? throw new InvalidOperationException("Completed workflow has no output.");
    var sourceHistory = await ReadHistory(source, historyClient, configuration, cancellationToken);
    var rerunHistory = await ReadHistory(rerun, historyClient, configuration, cancellationToken);
    var executions = recorder.Read(rerun);
    var results = new[] { output.Load, output.Validate, output.Inventory, output.Charge, output.Save };
    var rows = results.Select(result =>
    {
        var original = sourceHistory.LastOrDefault(entry =>
            entry.EventType == nameof(HistoryEvent.EventTypeOneofCase.TaskCompleted)
            && entry.ActivityName == result.ActivityName)?.Output?.Deserialize<ActivityResult>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return new
        {
            result.ActivityName,
            sourceInvocationId = original?.InvocationId,
            rerunOutputInvocationId = result.InvocationId,
            sourceCompletedAt = original?.CompletedAt,
            outputCompletedAt = result.CompletedAt,
            result.ExecutedByInstanceId,
            observedWorkerInvocationsInRerun = executions.Count(entry => entry.ActivityName == result.ActivityName),
            resultWasProducedInRerun = result.ExecutedByInstanceId == rerun,
            sameResultAsSource = original is null ? (bool?)null : original.InvocationId == result.InvocationId,
        };
    }).ToArray();

    return Results.Ok(new { sourceInstanceId = source, rerunInstanceId = rerun, rows, sourceHistory, rerunHistory, executions });
});

app.Run();

static async Task<IResult> Rerun(
    string sourceId, uint? requestedEventId, DaprWorkflowClient workflows,
    TaskHubSidecarService.TaskHubSidecarServiceClient historyClient,
    IConfiguration configuration, CancellationToken cancellationToken)
{
    var state = await workflows.GetWorkflowStateAsync(sourceId, cancellation: cancellationToken);
    if (state.RuntimeStatus is not WorkflowRuntimeStatus.Failed and not WorkflowRuntimeStatus.Terminated)
    {
        return Results.Conflict("Wait for the process to fail at Save before requesting rerun.");
    }

    var history = await ReadHistory(sourceId, historyClient, configuration, cancellationToken);
    var schedules = history.Where(entry =>
        entry.EventType == nameof(HistoryEvent.EventTypeOneofCase.TaskScheduled)
        && (requestedEventId is null
            ? entry.ActivityName == nameof(RerunInventoryActivity)
            : entry.EventId == requestedEventId.Value)).ToArray();
    if (schedules.Length != 1)
    {
        return Results.BadRequest("Choose a TaskScheduled event ID from this process's native history.");
    }

    var selected = schedules[0];
    var eventId = checked((uint)selected.EventId);
    var rerunId = await workflows.RerunWorkflowFromEventAsync(sourceId, eventId,
        new RerunWorkflowFromEventOptions { NewInstanceId = $"rerun-{Guid.NewGuid():N}" }, cancellationToken);

    return Results.Ok(new { sourceInstanceId = sourceId, rerunInstanceId = rerunId, eventId, selected.ActivityName });
}

static async Task<HistoryEntry[]> ReadHistory(
    string instanceId, TaskHubSidecarService.TaskHubSidecarServiceClient client,
    IConfiguration configuration, CancellationToken cancellationToken)
{
    var headers = new Metadata();
    var token = DaprDefaults.GetDefaultDaprApiToken(configuration);
    if (string.IsNullOrWhiteSpace(token) is false)
    {
        headers.Add("dapr-api-token", token);
    }

    var response = await client.GetInstanceHistoryAsync(new GetInstanceHistoryRequest { InstanceId = instanceId },
        new CallOptions(headers: headers, cancellationToken: cancellationToken));
    var names = response.Events.Where(entry => entry.TaskScheduled is not null)
        .ToDictionary(entry => entry.EventId, entry => entry.TaskScheduled.Name);

    return response.Events.Select((entry, position) =>
    {
        int? scheduleId = entry.TaskScheduled is not null ? entry.EventId
            : entry.TaskCompleted?.TaskScheduledId ?? entry.TaskFailed?.TaskScheduledId;
        var name = scheduleId is not null && names.TryGetValue(scheduleId.Value, out var activityName)
            ? activityName : null;
        var result = entry.TaskCompleted?.Result;

        return new HistoryEntry(position, entry.EventId, entry.EventTypeCase.ToString(),
            entry.Timestamp?.ToDateTime(), scheduleId, name,
            entry.TaskScheduled?.TaskExecutionId ?? entry.TaskCompleted?.TaskExecutionId ?? entry.TaskFailed?.TaskExecutionId,
            string.IsNullOrWhiteSpace(result) ? null : JsonSerializer.Deserialize<JsonElement>(result));
    }).ToArray();
}
