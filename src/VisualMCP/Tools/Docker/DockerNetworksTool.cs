using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerNetworksTool
{
    [McpServerTool(Name = "docker_networks"), Description(
        "List the Docker networks (name, id, driver, scope). Read-only.")]
    public static Task<object> DockerNetworks(IDockerService docker) => docker.NetworksAsync();
}
