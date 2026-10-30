using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerStatsTool
{
    [McpServerTool(Name = "docker_stats"), Description(
        "One-shot resource sample for the running containers: CPU %, memory used/limit, network and block I/O, " +
        "PIDs (docker stats --no-stream). Read-only.")]
    public static Task<object> DockerStats(IDockerService docker) => docker.StatsAsync();
}
