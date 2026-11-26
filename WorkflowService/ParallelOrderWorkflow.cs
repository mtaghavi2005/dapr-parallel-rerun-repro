using Dapr.Workflow;

namespace ParallelRerunRepro;

public sealed class ParallelOrderWorkflow : Workflow<OrderInput, WorkflowOutput>
{
    public override async Task<WorkflowOutput> RunAsync(WorkflowContext context, OrderInput input)
    {
        var load = await context.CallActivityAsync<ActivityResult>(nameof(RerunLoadRequestActivity), input);

        // Invocation order fixes the schedule order. Neither branch is awaited before both are scheduled.
        var validateTask = context.CallActivityAsync<ActivityResult>(nameof(RerunValidateActivity), input);
        var inventoryTask = context.CallActivityAsync<ActivityResult>(nameof(RerunInventoryActivity), input);
        await Task.WhenAll(validateTask, inventoryTask);

        var charge = await context.CallActivityAsync<ActivityResult>(nameof(RerunChargeActivity), input);
        var save = await context.CallActivityAsync<ActivityResult>(nameof(RerunSaveActivity), input);

        return new WorkflowOutput(load, await validateTask, await inventoryTask, charge, save);
    }
}
