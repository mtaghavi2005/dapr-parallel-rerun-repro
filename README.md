# Parallel workflow rerun: execution evidence versus Catalyst graph

A small **plain Dapr .NET Workflow** reproduction with an Aspire dashboard for
traces and structured logs. It has no BRAIN dependency, custom SDK fork, activity
adapter, or factory decorator.

## What to show Diagrid

```text
                           +--> Validate (slow) --+
Load request --> parallel -+                     +--> Charge --> Save
                           +--> Inventory (fast) -+              source fails
```

1. Validate is scheduled before Inventory, but finishes after Inventory is scheduled.
2. Both activities have completed before the original workflow fails at Save.
3. Rerun the failed workflow from **Inventory's scheduling event** with unchanged input.
4. Inspect the new instance in Catalyst and compare it with the worker's logs,
   traces, and returned activity tokens.

The behavior previously observed was that Catalyst dimmed Validate even though
Validate executed again in the rerun. This project independently checks the
execution behavior. **The visual mismatch must be checked in Catalyst itself**;
a local Dapr run cannot validate Catalyst's graph rendering.

Expected execution results for the reproduced behavior:

| Activity | Rerun result |
|---|---|
| Load request | Reused: original invocation token and original instance ID |
| Validate | Executed again: new token, new instance ID, new worker log and span |
| Inventory | Executed again: selected rerun point |
| Charge | Executed again after the join |
| Save | Executed again; succeeds in the new instance |

Each token is generated **inside the activity body**, using `Guid.NewGuid()`.
Workflow replay does not generate these tokens or execution logs. Schedule IDs
and Dapr task-execution keys may remain unchanged; they alone are not proof of reuse.

## Prerequisites

- .NET 10 SDK. Aspire packages are pinned in the AppHost; no Aspire workload is required.
- Dapr CLI and runtime supporting workflow rerun. The sample pins Dapr .NET SDK 1.19.1.
- For local mode: Docker Desktop and initialized Dapr infrastructure (Redis on
  `6379`, placement on `50005`, scheduler on `50006`). Run `dapr init` once if needed.
- For Catalyst mode: a current Diagrid CLI supporting `dev run --id` and
  `--skip-managed-pubsub`, a Catalyst account, and `diagrid login`.
- Optional: Python 3 for the unattended verification script; PowerShell 7.1+
  for the cloud-project setup script.

CLI installation: [Dapr](https://docs.dapr.io/getting-started/install-dapr-cli/),
[Diagrid](https://docs.diagrid.io/getting-started/install-cli/).

## Run locally with Aspire

From this directory:

```bash
dotnet build ParallelRerunRepro.slnx
dotnet run --project AppHost --launch-profile http --no-build
```

Open the **Aspire dashboard login URL printed in the terminal**. Then open
[the demonstration page](http://localhost:5260) using the workflow-service resource link.

1. Click **Start process**; wait for the intended Save failure.
2. Click **Rerun from Inventory**; wait for completion.
3. Click **Compare execution evidence**.

To choose another boundary, enter one of the listed native scheduling IDs in
**Rerun from a specific event ID**, then click **Rerun from event ID**. The source
must have failed first. Event ID `0` is valid; use the actual IDs listed on the page.

4. In Aspire, open **Structured logs** for workflow-service. Filter for
   `ACTIVITY_EXECUTED` and the rerun instance ID shown on the demonstration page.
5. Open a log's trace link, or use **Traces** and find `Execute RerunValidateActivity`.
   The span includes `workflow.instance.id`, `workflow.scheduled_event.id`, and
   `demo.invocation.id`.
6. Download the evidence JSON before the call ends.

The default local ports are: demonstration page `5260`, Aspire dashboard `19288`,
OTLP `19389`, resource service `19390`, Dapr HTTP `3521`, and Dapr gRPC `55061`.
Stop the AppHost with `Ctrl+C`; it manages its worker and local sidecar.

### Run the same checks automatically

With Aspire still running:

```bash
python3 scripts/verify.py --label local
```

The script creates a fresh source, waits for failure, discovers Inventory's actual
native schedule, reruns, verifies result provenance and worker invocations, and
saves source/rerun native history under `artifacts/`. It also checks the required
parallel scheduling/completion order. It does not hardcode event ID `2`.

For manual requests in Rider, open [requests.http](requests.http).

## Run the same worker against Catalyst, with Aspire telemetry

Stop the local AppHost first. Build the solution as above, then authenticate:

```bash
diagrid login
```

Create a dedicated cloud project and app, or reuse a project you choose:

```powershell
pwsh ./scripts/setup-catalyst.ps1
# Or choose your own project name:
pwsh ./scripts/setup-catalyst.ps1 -ProjectName your-project-name
```

The setup script creates cloud resources in the CLI's selected organization and
default region. It never creates management keys or writes credentials. You can
preview the operation with `-WhatIf` or select a region with `-Region`.

Start Aspire in Catalyst mode, specifying the project configured above:

```bash
dotnet run --project AppHost --launch-profile http --no-build -- \
  --UseCatalyst=true --CatalystProject=parallel-rerun-repro
```

For an alternate CLI binary, add `--DiagridCli=/absolute/path/to/diagrid`.
For an alternate app identity, add `--CatalystAppId=your-app-id`.

In this mode Aspire runs `diagrid dev run`, which connects the local worker to
Catalyst. **It does not start a local Dapr sidecar.** The CLI supplies the worker
connection settings; you do not need BRAIN's management environment variables.
Telemetry from the .NET worker is still exported to Aspire. Remote runtime spans
depend on Catalyst's own tracing configuration.

Use the same demonstration page and buttons, or run:

```bash
python3 scripts/verify.py --label catalyst
```

In Catalyst, open the `parallel-rerun-repro` app in your chosen project, find the
new instance ID shown on the page, and compare:

- **Graph:** whether Validate is dimmed alongside reused Load request.
- **History:** Validate's new scheduling/completion times and new result token.
- **Aspire:** Validate's worker log and span under the rerun instance ID.

## Notes for the call

- Ask whether the graph's dimming means “outside the selected downstream path”
  or “result reused.” Those meanings differ for parallel siblings.
- This sample uses ordinary `Workflow<TInput,TOutput>`, `WorkflowActivity<TInput,TOutput>`,
  `CallActivityAsync`, `Task.WhenAll`, and `RerunWorkflowFromEventAsync`.
- The source fails because Save sees the original instance ID carried in the
  input. Save succeeds under the new instance ID. There is no input replacement,
  automatic retry policy, external service, cache, or shared recovery flag.
- The worker execution list is in memory. Keep the worker running during the
  demonstration. Native history and output provenance provide independent
  evidence; export the JSON for sharing.
- This is an execution/visualization reproduction, not a claim that workflow
  replay is broken. Record the actual Catalyst result and runtime version used.
