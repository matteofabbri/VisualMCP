using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerRemoveTool
{
    [McpServerTool(Name = "docker_rm"), Description(
        "Delete a container. A running container needs force=true; volumes=true also removes the anonymous volumes " +
        "it created (named volumes are never touched). Irreversible: the container's writable layer is lost. " +
        "Use this INSTEAD OF shell 'docker rm'.")]
    public static Task<object> DockerRemove(
        IDockerService docker,
        [Description("Container name or id.")] string container,
        [Description("Kill it first if it is running. Default: false.")] bool force = false,
        [Description("Also remove its anonymous volumes. Default: false.")] bool volumes = false)
        => docker.RemoveAsync(container, force, volumes);
}
