using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerVolumesTool
{
    [McpServerTool(Name = "docker_volumes"), Description(
        "List the Docker volumes (name, driver, mountpoint). Read-only.")]
    public static Task<object> DockerVolumes(IDockerService docker) => docker.VolumesAsync();
}
