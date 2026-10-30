using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerPsTool
{
    [McpServerTool(Name = "docker_ps"), Description(
        "List containers (REST-style): id, name, image, state, status, ports, labels. By default only the running " +
        "ones; all=true also lists the stopped ones. filter uses Docker's syntax, e.g. 'status=exited' or " +
        "'name=api'. Use this INSTEAD OF shell 'docker ps'. Read-only.")]
    public static Task<object> DockerPs(
        IDockerService docker,
        [Description("Include stopped containers (docker ps -a). Default: false.")] bool all = false,
        [Description("Docker --filter expression, e.g. 'status=running' or 'name=db'.")] string? filter = null)
        => docker.PsAsync(all, filter);
}
