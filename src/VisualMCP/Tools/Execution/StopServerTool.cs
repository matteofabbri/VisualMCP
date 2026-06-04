using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class StopServerTool
{
    [McpServerTool(Name = "stop_server"), Description(
        "Stop running VisualMCP server process(es) to release locks on the built DLL/exe so a rebuild or " +
        "re-publish can overwrite the files — without needing a shell 'Stop-Process' command. " +
        "By default it stops every OTHER instance and leaves the process serving THIS call alive so the " +
        "result can be returned. Set includeSelf=true to also stop the current process: it returns the " +
        "result first, then exits about a second later, and Claude Code relaunches it on the next call.")]
    public static object StopServer(
        [Description("Also stop the current server process handling this call (it replies first, then exits; Claude Code relaunches it). Default: false.")] bool includeSelf = false)
        => StopServerImpl.Run(includeSelf);
}
