using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerLogsTool
{
    [McpServerTool(Name = "docker_logs"), Description(
        "Read a container's logs: the last N lines (default 200), optionally with timestamps or from a moment in " +
        "time. Works on stopped containers too. Use this INSTEAD OF shell 'docker logs'. Read-only.")]
    public static Task<object> DockerLogs(
        IDockerService docker,
        [Description("Container name or id.")] string container,
        [Description("How many trailing lines to return (default 200, max 10000).")] int tail = 200,
        [Description("Prefix each line with its timestamp. Default: false.")] bool timestamps = false,
        [Description("Only logs since this moment: '10m', '2026-10-29T10:00:00' or a unix timestamp.")] string? since = null)
        => docker.LogsAsync(container, tail, timestamps, since);
}
