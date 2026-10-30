using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerRunTool
{
    [McpServerTool(Name = "docker_run"), Description(
        "Create and start a container from an image, with its configuration: name, published ports, environment " +
        "variables, volume mounts, network and command. Detached by default (returns the container id). " +
        "Use this INSTEAD OF shell 'docker run'. The image must already exist locally — call docker_pull first " +
        "unless you know it is there.")]
    public static Task<object> DockerRun(
        IDockerService docker,
        [Description("Image reference, e.g. 'postgres:16'.")] string image,
        [Description("Container name. Omit to let Docker generate one.")] string? name = null,
        [Description("Port mappings, e.g. [\"8080:80\", \"127.0.0.1:5432:5432\"].")] string[]? ports = null,
        [Description("Environment variables, e.g. [\"POSTGRES_PASSWORD=secret\", \"TZ=UTC\"].")] string[]? env = null,
        [Description("Volume mounts, e.g. [\"pgdata:/var/lib/postgresql/data\", \"./config:/etc/app:ro\"].")] string[]? volumes = null,
        [Description("Command run inside the container, verbatim (e.g. \"redis-server --appendonly yes\").")] string? command = null,
        [Description("Docker network to attach to.")] string? network = null,
        [Description("Run in the background. Default: true.")] bool detach = true,
        [Description("Remove the container automatically when it stops (--rm). Default: false.")] bool removeWhenStopped = false)
        => docker.RunContainerAsync(image, name, ports, env, volumes, command, network, detach, removeWhenStopped);
}
