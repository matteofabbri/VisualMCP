using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerStatusTool
{
    [McpServerTool(Name = "docker_status"), Description(
        "Check Docker availability and list containers: reports whether the Docker engine is reachable, " +
        "the client/server versions, and the running containers (name, image, status, ports). Use this " +
        "INSTEAD OF shell 'docker version' / 'docker ps'. Read-only.")]
    public static Task<object> DockerStatus(
        [Description("Include stopped containers too (docker ps -a). Default: false.")] bool includeStopped = false)
        => DockerStatusImpl.RunAsync(includeStopped);
}
