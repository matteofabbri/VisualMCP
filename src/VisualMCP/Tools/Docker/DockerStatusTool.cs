using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using VisualMCP.Tools.Execution;

namespace VisualMCP.Tools.Docker;

[McpServerToolType]
public static class DockerStatusTool
{
    [McpServerTool(Name = "docker_status"), Description(
        "Check Docker availability and list containers: reports whether the Docker engine is reachable, " +
        "the client/server versions, and the running containers (name, image, status, ports). Use this " +
        "INSTEAD OF shell 'docker version' / 'docker ps'. Read-only.")]
    public static async Task<object> DockerStatus(
        [Description("Include stopped containers too (docker ps -a). Default: false.")] bool includeStopped = false)
    {
        var dir = Directory.GetCurrentDirectory();

        var (vExit, vTimed, vOut, vErr, _) =
            await ProcessRunner.RunAsync("docker", "version --format \"{{json .}}\"", dir, 20);

        if (vTimed)
            return new { available = false, reason = "docker version timed out (engine starting?)." };

        string? client = null, server = null;
        if (vExit == 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(vOut.Trim());
                client = Prop(doc.RootElement, "Client", "Version");
                if (doc.RootElement.TryGetProperty("Server", out var s) && s.ValueKind == JsonValueKind.Object)
                    server = s.TryGetProperty("Version", out var sv) ? sv.GetString() : null;
            }
            catch { /* unparseable */ }
        }

        if (server is null)
            return new
            {
                available = false,
                clientVersion = client,
                reason = vExit == 0
                    ? "Docker client is installed but the engine is not reachable (is Docker Desktop running?)."
                    : $"docker is not available: {vErr.Trim()}",
            };

        var psArgs = "ps --format \"{{json .}}\"" + (includeStopped ? " -a" : "");
        var (_, _, psOut, _, _) = await ProcessRunner.RunAsync("docker", psArgs, dir, 20);

        var containers = new List<object>();
        foreach (var line in psOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var d = JsonDocument.Parse(line.Trim());
                containers.Add(new
                {
                    name   = Get(d.RootElement, "Names"),
                    image  = Get(d.RootElement, "Image"),
                    status = Get(d.RootElement, "Status"),
                    ports  = Get(d.RootElement, "Ports"),
                });
            }
            catch { /* skip non-JSON line */ }
        }

        return new
        {
            available = true,
            clientVersion = client,
            serverVersion = server,
            includeStopped,
            containerCount = containers.Count,
            containers,
        };
    }

    private static string? Get(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static string? Prop(JsonElement el, string obj, string name) =>
        el.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.GetString() : null;
}
