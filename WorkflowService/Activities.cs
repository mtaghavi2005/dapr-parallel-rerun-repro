using Dapr.Workflow;

namespace ParallelRerunRepro;

public sealed class RerunLoadRequestActivity : WorkflowActivity<OrderInput, ActivityResult>
{
    private readonly ExecutionRecorder _recorder;

    public RerunLoadRequestActivity(ExecutionRecorder recorder)
    {
        _recorder = recorder;
    }

    public override Task<ActivityResult> RunAsync(WorkflowActivityContext context, OrderInput input)
    {
        return _recorder.ExecuteAsync(context, nameof(RerunLoadRequestActivity), 100);
    }
}

public sealed class RerunValidateActivity : WorkflowActivity<OrderInput, ActivityResult>
{
    private readonly ExecutionRecorder _recorder;

    public RerunValidateActivity(ExecutionRecorder recorder)
    {
        _recorder = recorder;
    }

    public override Task<ActivityResult> RunAsync(WorkflowActivityContext context, OrderInput input)
    {
        return _recorder.ExecuteAsync(context, nameof(RerunValidateActivity), input.DelaySeconds * 1000);
    }
}

public sealed class RerunInventoryActivity : WorkflowActivity<OrderInput, ActivityResult>
{
    private readonly ExecutionRecorder _recorder;

    public RerunInventoryActivity(ExecutionRecorder recorder)
    {
        _recorder = recorder;
    }

    public override Task<ActivityResult> RunAsync(WorkflowActivityContext context, OrderInput input)
    {
        return _recorder.ExecuteAsync(context, nameof(RerunInventoryActivity), 150);
    }
}

public sealed class RerunChargeActivity : WorkflowActivity<OrderInput, ActivityResult>
{
    private readonly ExecutionRecorder _recorder;

    public RerunChargeActivity(ExecutionRecorder recorder)
    {
        _recorder = recorder;
    }

    public override Task<ActivityResult> RunAsync(WorkflowActivityContext context, OrderInput input)
    {
        return _recorder.ExecuteAsync(context, nameof(RerunChargeActivity), 100);
    }
}

public sealed class RerunSaveActivity : WorkflowActivity<OrderInput, ActivityResult>
{
    private readonly ExecutionRecorder _recorder;

    public RerunSaveActivity(ExecutionRecorder recorder)
    {
        _recorder = recorder;
    }

    public override Task<ActivityResult> RunAsync(WorkflowActivityContext context, OrderInput input)
    {
        // Fault injection is activity-local: original instance fails, rerun instance succeeds.
        return _recorder.ExecuteAsync(context, nameof(RerunSaveActivity), input.DelaySeconds * 1000,
            fail: context.InstanceId == input.SourceInstanceId);
    }
}
