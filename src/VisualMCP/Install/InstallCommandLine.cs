namespace VisualMCP.Install;

/// <summary>
/// The two install-time modes of this executable, dispatched before the server starts: the routing
/// guard (<c>--hook pre-tool-use</c>, reading the client's JSON payload on stdin) and the installer's
/// configuration edit (<c>--config &lt;mode&gt; …</c>). Neither loads MSBuild, a workspace or the MCP
/// transport, so a hook call costs a process start and nothing else.
/// </summary>
public static class InstallCommandLine
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        switch (args[0])
        {
            case "--hook":
                if (args.Length > 1 && args[1] == "pre-tool-use")
                {
                    exitCode = RoutingGuard
                        .RunHookAsync(Console.OpenStandardInput(), Console.Out)
                        .GetAwaiter()
                        .GetResult();
                }
                else
                {
                    // Never fall through to the server: it would wait on stdin for an MCP client.
                    Console.Error.WriteLine("usage: --hook pre-tool-use");
                    exitCode = 2;
                }
                return true;

            case "--config":
                // No mode is the usage error the installer's capability probe looks for.
                exitCode = ConfigEditor.Run(args.Length > 1 ? args[1..] : []);
                return true;

            default:
                return false;
        }
    }
}
