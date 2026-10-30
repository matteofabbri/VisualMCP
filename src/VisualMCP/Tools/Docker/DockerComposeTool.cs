using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerComposeTool
{
    [McpServerTool(Name = "docker_compose"), Description(
        "Manage a compose stack: up (detached), down, ps, logs, restart, stop, start, pull, build. Pass file for a " +
        "specific compose file, projectName to keep stacks apart, service to target one service. Returns the " +
        "command's output. Use this INSTEAD OF shell 'docker compose ...'.")]
    public static Task<object> DockerCompose(
        IDockerService docker,
        [Description("up | down | ps | logs | restart | stop | start | pull | build")] string action,
        [Description("Path to the compose file (default: docker-compose.yml in the working directory).")] string? file = null,
        [Description("Compose project name (isolates containers/volumes of the stack).")] string? projectName = null,
        [Description("Target a single service instead of the whole stack.")] string? service = null)
        => docker.ComposeAsync(action, file, projectName, service);
}
