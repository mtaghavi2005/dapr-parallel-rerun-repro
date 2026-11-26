using CommunityToolkit.Aspire.Hosting.Dapr;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
var catalyst = builder.Configuration.GetValue<bool>("UseCatalyst");

if (catalyst)
{
    var project = builder.Configuration["CatalystProject"] ?? "parallel-rerun-repro";
    var appId = builder.Configuration["CatalystAppId"] ?? "parallel-rerun-repro";
    var cli = builder.Configuration["DiagridCli"] ?? "diagrid";

    // The CLI connects the local worker to Catalyst. No local Dapr sidecar is started in this mode.
    builder.AddExecutable("workflow-service", cli, root,
            "dev", "run", "--project", project, "--id", appId,
            "--app-port", "5260", "--skip-managed-pubsub", "--yes", "--",
            "dotnet", "run", "--project", "WorkflowService", "--no-build", "--no-launch-profile",
            "--urls", "http://localhost:5260")
        .WithHttpEndpoint(port: 5260, targetPort: 5260, name: "http", isProxied: false)
        .WithOtlpExporter();
}
else
{
    builder.AddProject<Projects.WorkflowService>("workflow-service", launchProfileName: "http")
        .WithHttpEndpoint(port: 5260, targetPort: 5260, name: "http", isProxied: false)
        .WithDaprSidecar(new DaprSidecarOptions
        {
            AppId = "parallel-rerun-repro",
            AppPort = 5260,
            DaprHttpPort = 3521,
            DaprGrpcPort = 55061,
            PlacementHostAddress = "localhost:50005",
            SchedulerHostAddress = "localhost:50006",
            ResourcesPaths = [Path.Combine(root, "dapr", "components")],
            Config = Path.Combine(root, "dapr", "config.yaml"),
        });
}

builder.Build().Run();
