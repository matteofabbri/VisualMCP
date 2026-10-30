using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerImagesTool
{
    [McpServerTool(Name = "docker_images"), Description(
        "List the local images (repository, tag, id, size, age); pass a reference to inspect one image's JSON " +
        "instead. Use this INSTEAD OF shell 'docker images' / 'docker image inspect'. Read-only.")]
    public static Task<object> DockerImages(
        IDockerService docker,
        [Description("Image reference to inspect, e.g. 'postgres:16'. Omit to list all images.")] string? reference = null)
        => docker.ImagesAsync(reference);
}
