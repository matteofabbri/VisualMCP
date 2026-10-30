using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerPruneTool
{
    [McpServerTool(Name = "docker_prune"), Description(
        "Reclaim disk by deleting unused objects: stopped containers, dangling images, unused volumes/networks, the " +
        "build cache, or 'system' for several of them. Destructive and irreversible, so it refuses unless " +
        "confirm=true. Use this INSTEAD OF shell 'docker <x> prune -f'.")]
    public static Task<object> DockerPrune(
        IDockerService docker,
        [Description("containers | images | volumes | networks | builder | system")] string target,
        [Description("Must be true: confirms the deletion. Default: false (just refuses).")] bool confirm = false)
        => docker.PruneAsync(target, confirm);
}
