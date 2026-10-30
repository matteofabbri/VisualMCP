using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerPullTool
{
    [McpServerTool(Name = "docker_pull"), Description(
        "Pull an image from the registry into the local engine. Can take minutes on a slow link (600s limit); " +
        "returns the tail of the progress output. Use this INSTEAD OF shell 'docker pull'.")]
    public static Task<object> DockerPull(
        IDockerService docker,
        [Description("Image reference, e.g. 'postgres:16' or 'ghcr.io/org/app:latest'.")] string image)
        => docker.PullAsync(image);
}
