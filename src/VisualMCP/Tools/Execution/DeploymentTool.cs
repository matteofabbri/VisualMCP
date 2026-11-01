using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class DeploymentTool
{
    [McpServerTool(Name = "deploy_target"), Description(
        "Deploy a built project to IIS or Azure — the two destinations this server did not have. " +
        "target=iis runs 'dotnet publish -p:PublishProfile=<profile>', so the Web SDK's MSDeploy target " +
        "sends the files with the profile's MSDeployServiceURL/DeployIisAppPath; the profile must exist " +
        "under Properties/PublishProfiles and mention MSDeploy (write it with publish_profile). " +
        "target=azure-webapp publishes the project and runs 'az webapp deploy --src-path … --type zip' " +
        "(needs resourceGroup and appName; sourcePath optionally reuses an existing build). " +
        "target=azure-staticwebapp runs 'az staticwebapp deploy --name … --source <folder>'. The call is " +
        "REFUSED without confirm=true because it changes a machine that is not this one, and a missing " +
        "msdeploy/az (or no network) is a structured answer with the command that was attempted.")]
    public static Task<DeploymentResult> DeployTarget(
        IDeploymentService deployments,
        [Description("iis, azure-webapp or azure-staticwebapp.")] string target,
        [Description("The project to deploy (exact name from the loaded solution).")] string projectName,
        [Description("For iis: the publish profile name (Properties/PublishProfiles/<profile>.pubxml).")]
        string? profile = null,
        [Description("For azure-webapp: the resource group.")] string? resourceGroup = null,
        [Description("For azure-webapp / azure-staticwebapp: the app name.")] string? appName = null,
        [Description("For azure-*: an existing build to deploy (a zip or a folder); azure-webapp publishes first when omitted.")]
        string? sourcePath = null,
        [Description("Must be true: the deployment changes a remote machine.")] bool confirm = false,
        [Description("Timeout in seconds for each command (default 600).")] int timeoutSeconds = 600)
        => deployments.DeployAsync(target, projectName, profile, resourceGroup, appName, sourcePath, confirm, timeoutSeconds);
}
