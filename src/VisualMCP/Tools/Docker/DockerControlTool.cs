using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Docker;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerControlTool
{
    [McpServerTool(Name = "docker_control"), Description(
        "Start, stop, restart, pause, unpause or kill a container, then report its resulting state. " +
        "Use this INSTEAD OF shelling out to 'docker start|stop|restart|pause|unpause|kill'.")]
    public static Task<object> DockerControl(
        IDockerService docker,
        [Description("start | stop | restart | pause | unpause | kill")] string action,
        [Description("Container name or id.")] string container)
        => docker.ControlAsync(action, container);
}
