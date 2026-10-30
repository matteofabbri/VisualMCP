using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerRemoveImageTool
{
    [McpServerTool(Name = "docker_image_rm"), Description(
        "Delete a local image. It fails while a container (running or stopped) still uses it, unless force=true. " +
        "Use this INSTEAD OF shell 'docker rmi'.")]
    public static Task<object> DockerRemoveImage(
        IDockerService docker,
        [Description("Image reference or id.")] string image,
        [Description("Remove even if containers reference it. Default: false.")] bool force = false)
        => docker.RemoveImageAsync(image, force);
}
