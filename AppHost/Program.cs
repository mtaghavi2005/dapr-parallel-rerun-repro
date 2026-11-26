using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.Dapr;
using Diagrid.Aspire.Hosting.Catalyst;
using Diagrid.Aspire.Hosting.Catalyst.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var builder = DistributedApplication.CreateBuilder(args);
var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
var diagrid = builder.Configuration.GetSection("Diagrid");
var catalyst = diagrid.GetValue<bool>("Enabled");

if (catalyst)
{
    var projectName = diagrid["Project"];

    if (string.IsNullOrWhiteSpace(projectName))
    {
        throw new InvalidOperationException("Set Diagrid:Project to your existing Diagrid project name in AppHost configuration or user secrets.");
    }

    var organization = diagrid["Organization"];
    const string appId = "parallel-rerun-repro";
    if (string.IsNullOrWhiteSpace(organization))
    {
        throw new InvalidOperationException("Set Diagrid:Organization in AppHost user secrets.");
    }

    var selectOrganization = new ProcessStartInfo("diagrid")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    selectOrganization.ArgumentList.Add("org");
    selectOrganization.ArgumentList.Add("use");
    selectOrganization.ArgumentList.Add(organization);

    using (var selection = Process.Start(selectOrganization)
        ?? throw new InvalidOperationException("Could not start the Diagrid CLI. Install it and add it to PATH."))
    {
        var output = selection.StandardOutput.ReadToEndAsync();
        var error = selection.StandardError.ReadToEndAsync();
        await selection.WaitForExitAsync();
        await Task.WhenAll(output, error);

        if (selection.ExitCode != 0)
        {
            throw new InvalidOperationException("Could not select the configured Diagrid organization. Run diagrid login and check Diagrid:Organization.");
        }
    }
    var project = builder.AddCatalystProject(projectName,
        descriptor: new ProjectDescriptor { EnableManagedWorkflow = true });

    builder.AddProject<Projects.WorkflowService>(appId, launchProfileName: "http")
        .WithHttpEndpoint(port: 5260, targetPort: 5260, name: "http", isProxied: false)
        .WithCatalyst(project);

    // Catalyst 1.0.0 publishes Active immediately after Finished, so its completion waits can miss readiness.
    // Wait for its persistent Active state instead; the worker awaits its credentials internally.
    foreach (var resource in builder.Resources)
    {
        foreach (var wait in resource.Annotations.OfType<WaitAnnotation>()
            .Where(wait => wait.Resource == project.Resource).ToArray())
        {
            resource.Annotations.Remove(wait);
        }
    }

    var proxy = builder.Resources.OfType<ExecutableResource>()
        .Single(resource => resource.Name == $"{appId}-catalyst-proxy");
    builder.CreateResourceBuilder(proxy).WithEnvironment(async context =>
    {
        var notifications = context.ExecutionContext.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        await notifications.WaitForResourceAsync(project.Resource.Name, KnownResourceStates.Active, context.CancellationToken);
    });
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
