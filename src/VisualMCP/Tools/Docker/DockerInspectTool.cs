using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerInspectTool
{
    [McpServerTool(Name = "docker_inspect"), Description(
        "Full configuration of a container as pretty JSON: environment, mounts, port bindings, networks, restart " +
        "policy, healthcheck, entrypoint/command. This is how you READ a container's configuration before changing " +
        "it. Use this INSTEAD OF shell 'docker inspect <name>'. Read-only.")]
    public static Task<object> DockerInspect(
        IDockerService docker,
        [Description("Container name or id.")] string container)
        => docker.InspectAsync(container);
}
