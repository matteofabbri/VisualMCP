using System.Text.Json;
using Snitch.Execution;

namespace Snitch.Docker;

/// <summary>
/// Docker operations over the local engine: containers (list, inspect, logs, lifecycle, remove,
/// run), images, networks, volumes, one-shot stats and docker compose. Every call degrades to a
/// structured <c>{ error }</c> when the engine is not reachable, so callers never see an exception.
/// </summary>
public interface IDockerService
{
    /// <summary>Engine availability plus the running (or all) containers.</summary>
    Task<object> RunAsync(bool includeStopped);

    Task<object> PsAsync(bool all, string? filter);
    Task<object> InspectAsync(string container);
    Task<object> LogsAsync(string container, int tail, bool timestamps, string? since);
    Task<object> ControlAsync(string action, string container);
    Task<object> RemoveAsync(string container, bool force, bool volumes);
    Task<object> RunContainerAsync(
        string image, string? name, string[]? ports, string[]? env, string[]? volumes,
        string? command, string? network, bool detach, bool removeWhenStopped);
    Task<object> ImagesAsync(string? reference);
    Task<object> PullAsync(string image);
    Task<object> RemoveImageAsync(string image, bool force);
    Task<object> NetworksAsync();
    Task<object> VolumesAsync();
    Task<object> StatsAsync();
    Task<object> ComposeAsync(string action, string? file, string? projectName, string? service);
    Task<object> PruneAsync(string target, bool confirm);
}

public sealed class DockerService : IDockerService
{
    private static readonly string[] LifecycleActions = ["start", "stop", "restart", "pause", "unpause", "kill"];
    private static readonly string[] ComposeActions = ["up", "down", "ps", "logs", "restart", "stop", "start", "pull", "build"];

    private sealed record ContainerInfo(
        string? Id, string? Name, string? Image, string State, string? Status, string? Ports, string? CreatedAt, string? Labels);

    // ── status ───────────────────────────────────────────────────────────────
    public async Task<object> RunAsync(bool includeStopped)
    {
        var (client, server, error) = await EngineAsync();
        if (server is null)
            return new { available = false, clientVersion = client, reason = error };

        var containers = await ContainersAsync(includeStopped, filter: null);

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

    // ── containers ───────────────────────────────────────────────────────────
    public async Task<object> PsAsync(bool all, string? filter)
    {
        if (await EngineErrorAsync() is { } error) return error;

        var containers = await ContainersAsync(all, filter);
        return new
        {
            all,
            filter,
            count = containers.Count,
            running = containers.Count(container => container.State == "running"),
            containers,
        };
    }

    public async Task<object> InspectAsync(string container)
    {
        if (string.IsNullOrWhiteSpace(container)) return new { error = "A container name or id is required." };
        if (LooksLikeFlag(container)) return new { error = $"'{container}' is not a container name." };
        if (await EngineErrorAsync() is { } error) return error;

        var (exitCode, _, stdout, stderr, _) = await RunAsync($"inspect {Quote(container)}", 30);
        if (exitCode != 0) return new { error = $"docker inspect failed: {stderr.Trim()}", container };

        return new
        {
            container,
            json = Pretty(stdout),
            note = "Full engine configuration: env, mounts, ports, network, healthcheck, restart policy.",
        };
    }

    public async Task<object> LogsAsync(string container, int tail, bool timestamps, string? since)
    {
        if (string.IsNullOrWhiteSpace(container)) return new { error = "A container name or id is required." };
        if (LooksLikeFlag(container)) return new { error = $"'{container}' is not a container name." };
        if (await EngineErrorAsync() is { } error) return error;

        if (tail < 1) tail = 200;
        if (tail > 10_000) tail = 10_000;

        var args = $"logs --tail {tail}";
        if (timestamps) args += " --timestamps";
        if (!string.IsNullOrWhiteSpace(since)) args += $" --since {Quote(since!)}";
        args += $" {Quote(container)}";

        var (exitCode, timedOut, stdout, stderr, _) = await RunAsync(args, 45);
        if (timedOut) return new { error = "docker logs timed out." };

        // docker writes container logs on stdout and its own diagnostics on stderr; a stopped
        // container still exits 0, so an error is only reported when there is no output at all.
        var text = string.Concat(stdout, stderr);
        return new
        {
            container,
            tail,
            timestamps,
            since,
            empty = string.IsNullOrWhiteSpace(text),
            logs = ProcessRunner.Truncate(text, 32_000),
            error = exitCode != 0 && string.IsNullOrWhiteSpace(text) ? $"docker logs failed: {stderr.Trim()}" : null,
        };
    }

    public async Task<object> ControlAsync(string action, string container)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!LifecycleActions.Contains(verb))
            return new { error = $"Unknown action '{action}'. Use one of: {string.Join(", ", LifecycleActions)}." };
        if (string.IsNullOrWhiteSpace(container)) return new { error = "A container name or id is required." };
        if (LooksLikeFlag(container)) return new { error = $"'{container}' is not a container name." };
        if (await EngineErrorAsync() is { } error) return error;

        var (exitCode, timedOut, stdout, stderr, _) = await RunAsync($"{verb} {Quote(container)}", 60);
        if (timedOut) return new { error = $"docker {verb} timed out (the container may not answer signals)." };
        if (exitCode != 0) return new { error = $"docker {verb} failed: {stderr.Trim()}", container };

        return new
        {
            container,
            action = verb,
            done = true,
            state = await StateOfAsync(container),
            output = ProcessRunner.Truncate((stdout + stderr).Trim(), 1000),
        };
    }

    public async Task<object> RemoveAsync(string container, bool force, bool volumes)
    {
        if (string.IsNullOrWhiteSpace(container)) return new { error = "A container name or id is required." };
        if (LooksLikeFlag(container)) return new { error = $"'{container}' is not a container name." };
        if (await EngineErrorAsync() is { } error) return error;

        var args = "rm";
        if (force) args += " --force";
        if (volumes) args += " --volumes";
        args += $" {Quote(container)}";

        var (exitCode, timedOut, stdout, stderr, _) = await RunAsync(args, 60);
        if (timedOut) return new { error = "docker rm timed out." };
        if (exitCode != 0) return new { error = $"docker rm failed: {stderr.Trim()}", container };

        return new
        {
            container,
            removed = true,
            force,
            anonymousVolumesRemoved = volumes,
            output = ProcessRunner.Truncate((stdout + stderr).Trim(), 1000),
        };
    }

    public async Task<object> RunContainerAsync(
        string image, string? name, string[]? ports, string[]? env, string[]? volumes,
        string? command, string? network, bool detach, bool removeWhenStopped)
    {
        if (string.IsNullOrWhiteSpace(image)) return new { error = "An image is required, e.g. 'nginx:latest'." };
        if (LooksLikeFlag(image)) return new { error = $"'{image}' is not an image reference." };
        if (name is not null && LooksLikeFlag(name)) return new { error = $"'{name}' is not a container name." };
        if (await EngineErrorAsync() is { } error) return error;

        var args = "run";
        if (detach) args += " -d";
        if (removeWhenStopped) args += " --rm";
        if (!string.IsNullOrWhiteSpace(name)) args += $" --name {Quote(name!)}";
        if (!string.IsNullOrWhiteSpace(network)) args += $" --network {Quote(network!)}";

        foreach (var port in ports ?? [])
            if (!string.IsNullOrWhiteSpace(port)) args += $" -p {Quote(port)}";

        foreach (var variable in env ?? [])
            if (!string.IsNullOrWhiteSpace(variable)) args += $" -e {Quote(variable)}";

        foreach (var mount in volumes ?? [])
            if (!string.IsNullOrWhiteSpace(mount)) args += $" -v {Quote(mount)}";

        args += $" {Quote(image)}";
        if (!string.IsNullOrWhiteSpace(command)) args += $" {command!.Trim()}";

        var (exitCode, timedOut, stdout, stderr, _) = await RunAsync(args, 180);
        if (timedOut) return new { error = "docker run timed out.", command = $"docker {args}" };
        if (exitCode != 0)
            return new { error = $"docker run failed: {stderr.Trim()}", command = $"docker {args}" };

        var id = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();

        return new
        {
            started = true,
            container = name ?? id,
            containerId = detach ? id : null,
            image,
            detached = detach,
            removeWhenStopped,
            ports = ports ?? [],
            env = env ?? [],
            volumes = volumes ?? [],
            state = id is null ? null : await StateOfAsync(id),
            command = $"docker {args}",
            note = detach ? null : "The command ran to completion (detach=false).",
        };
    }

    // ── images ───────────────────────────────────────────────────────────────
    public async Task<object> ImagesAsync(string? reference)
    {
        if (await EngineErrorAsync() is { } error) return error;

        // With a reference this inspects one image instead of listing them all.
        if (!string.IsNullOrWhiteSpace(reference))
        {
            if (LooksLikeFlag(reference)) return new { error = $"'{reference}' is not an image reference." };
            var (inspectExit, _, inspectOut, inspectErr, _) = await RunAsync($"image inspect {Quote(reference!)}", 30);
            if (inspectExit != 0) return new { error = $"docker image inspect failed: {inspectErr.Trim()}", image = reference };
            return new { image = reference, json = Pretty(inspectOut) };
        }

        var (exitCode, _, stdout, stderr, _) = await RunAsync($"images --format {Quote("{{json .}}")}", 30);
        if (exitCode != 0) return new { error = $"docker images failed: {stderr.Trim()}" };

        var images = ReadJsonLines(stdout, element => new
        {
            repository = Get(element, "Repository"),
            tag = Get(element, "Tag"),
            id = Get(element, "ID"),
            size = Get(element, "Size"),
            createdSince = Get(element, "CreatedSince"),
        });

        return new { count = images.Count, images };
    }

    public async Task<object> PullAsync(string image)
    {
        if (string.IsNullOrWhiteSpace(image)) return new { error = "An image is required, e.g. 'postgres:16'." };
        if (LooksLikeFlag(image)) return new { error = $"'{image}' is not an image reference." };
        if (await EngineErrorAsync() is { } error) return error;

        var (exitCode, timedOut, stdout, stderr, _) = await RunAsync($"pull {Quote(image)}", 600);
        if (timedOut) return new { error = "docker pull timed out after 600s (slow network?).", image };
        if (exitCode != 0) return new { error = $"docker pull failed: {stderr.Trim()}", image };

        var lines = string.Concat(stdout, stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();

        return new
        {
            image,
            pulled = true,
            steps = lines.Count,
            output = ProcessRunner.Truncate(string.Join('\n', lines.TakeLast(12)), 4000),
        };
    }

    public async Task<object> RemoveImageAsync(string image, bool force)
    {
        if (string.IsNullOrWhiteSpace(image)) return new { error = "An image is required." };
        if (LooksLikeFlag(image)) return new { error = $"'{image}' is not an image reference." };
        if (await EngineErrorAsync() is { } error) return error;

        var args = force ? $"rmi --force {Quote(image)}" : $"rmi {Quote(image)}";
        var (exitCode, _, stdout, stderr, _) = await RunAsync(args, 120);
        if (exitCode != 0) return new { error = $"docker rmi failed: {stderr.Trim()}", image };

        return new { image, removed = true, force, output = ProcessRunner.Truncate((stdout + stderr).Trim(), 1000) };
    }

    // ── networks / volumes / stats ───────────────────────────────────────────
    public async Task<object> NetworksAsync()
    {
        if (await EngineErrorAsync() is { } error) return error;

        var (exitCode, _, stdout, stderr, _) = await RunAsync($"network ls --format {Quote("{{json .}}")}", 30);
        if (exitCode != 0) return new { error = $"docker network ls failed: {stderr.Trim()}" };

        var networks = ReadJsonLines(stdout, element => new
        {
            name = Get(element, "Name"),
            id = Get(element, "ID"),
            driver = Get(element, "Driver"),
            scope = Get(element, "Scope"),
        });

        return new { count = networks.Count, networks };
    }

    public async Task<object> VolumesAsync()
    {
        if (await EngineErrorAsync() is { } error) return error;

        var (exitCode, _, stdout, stderr, _) = await RunAsync($"volume ls --format {Quote("{{json .}}")}", 30);
        if (exitCode != 0) return new { error = $"docker volume ls failed: {stderr.Trim()}" };

        var volumes = ReadJsonLines(stdout, element => new
        {
            name = Get(element, "Name"),
            driver = Get(element, "Driver"),
            mountpoint = Get(element, "Mountpoint"),
        });

        return new { count = volumes.Count, volumes };
    }

    public async Task<object> StatsAsync()
    {
        if (await EngineErrorAsync() is { } error) return error;

        var (exitCode, timedOut, stdout, stderr, _) =
            await RunAsync($"stats --no-stream --format {Quote("{{json .}}")}", 45);
        if (timedOut) return new { error = "docker stats timed out." };
        if (exitCode != 0) return new { error = $"docker stats failed: {stderr.Trim()}" };

        var stats = ReadJsonLines(stdout, element => new
        {
            container = Get(element, "Name"),
            cpu = Get(element, "CPUPerc"),
            memory = Get(element, "MemUsage"),
            memoryPercent = Get(element, "MemPerc"),
            network = Get(element, "NetIO"),
            blockIo = Get(element, "BlockIO"),
            pids = Get(element, "PIDs"),
        });

        return new { count = stats.Count, note = "One-shot sample (docker stats --no-stream).", stats };
    }

    // ── compose ──────────────────────────────────────────────────────────────
    public async Task<object> ComposeAsync(string action, string? file, string? projectName, string? service)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!ComposeActions.Contains(verb))
            return new { error = $"Unknown compose action '{action}'. Use one of: {string.Join(", ", ComposeActions)}." };
        if (await EngineErrorAsync() is { } error) return error;

        var args = "compose";
        if (!string.IsNullOrWhiteSpace(file)) args += $" -f {Quote(file!)}";
        if (!string.IsNullOrWhiteSpace(projectName)) args += $" -p {Quote(projectName!)}";
        args += $" {verb}";
        if (verb == "up" && string.IsNullOrWhiteSpace(service)) args += " -d";
        if (!string.IsNullOrWhiteSpace(service)) args += $" {Quote(service!)}";

        var timeout = verb is "up" or "build" or "pull" ? 600 : 120;
        var (exitCode, timedOut, stdout, stderr, _) = await RunAsync(args, timeout);
        if (timedOut) return new { error = $"docker compose {verb} timed out after {timeout}s." };

        var text = string.Concat(stdout, stderr);
        return new
        {
            action = verb,
            file,
            project = projectName,
            service,
            succeeded = exitCode == 0,
            command = $"docker {args}",
            output = ProcessRunner.Truncate(text.Trim(), 12_000),
            error = exitCode != 0 ? $"docker compose {verb} failed (exit {exitCode})." : null,
        };
    }

    // ── prune ────────────────────────────────────────────────────────────────
    public async Task<object> PruneAsync(string target, bool confirm)
    {
        var what = (target ?? string.Empty).Trim().ToLowerInvariant();
        if (what is not ("containers" or "images" or "volumes" or "networks" or "system" or "builder"))
            return new { error = "target must be one of: containers, images, volumes, networks, system, builder." };
        if (!confirm)
            return new { error = "Prune deletes data that cannot be recovered. Pass confirm=true to proceed.", target = what };

        if (await EngineErrorAsync() is { } error) return error;

        var args = what switch
        {
            "containers" => "container prune -f",
            "images" => "image prune -f",
            "volumes" => "volume prune -f",
            "networks" => "network prune -f",
            "builder" => "builder prune -f",
            _ => "system prune -f",
        };

        var (exitCode, timedOut, stdout, stderr, _) = await RunAsync(args, 180);
        if (timedOut) return new { error = "docker prune timed out." };
        if (exitCode != 0) return new { error = $"docker {args} failed: {stderr.Trim()}" };

        return new
        {
            target = what,
            command = $"docker {args}",
            output = ProcessRunner.Truncate((stdout + stderr).Trim(), 4000),
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private async Task<List<ContainerInfo>> ContainersAsync(bool all, string? filter)
    {
        var args = all ? "ps -a" : "ps";
        args += $" --format {Quote("{{json .}}")}";
        if (!string.IsNullOrWhiteSpace(filter)) args += $" --filter {Quote(filter!)}";

        var (_, _, stdout, _, _) = await RunAsync(args, 30);
        return ReadJsonLines(stdout, element => new ContainerInfo(
            Get(element, "ID"),
            Get(element, "Names"),
            Get(element, "Image"),
            Get(element, "State") ?? "unknown",
            Get(element, "Status"),
            Get(element, "Ports"),
            Get(element, "CreatedAt"),
            Get(element, "Labels")));
    }

    private async Task<string?> StateOfAsync(string container)
    {
        var (exitCode, _, stdout, _, _) = await RunAsync($"inspect --format \"{{{{.State.Status}}}}\" {Quote(container)}", 20);
        return exitCode == 0 ? stdout.Trim() : null;
    }

    private async Task<(string? client, string? server, string? error)> EngineAsync()
    {
        var (exitCode, timedOut, stdout, stderr, _) = await RunAsync("version --format \"{{json .}}\"", 20);

        if (timedOut) return (null, null, "docker version timed out (engine starting?).");

        string? client = null, server = null;
        if (exitCode == 0)
        {
            try
            {
                using var document = JsonDocument.Parse(stdout.Trim());
                client = Prop(document.RootElement, "Client", "Version");
                if (document.RootElement.TryGetProperty("Server", out var serverElement) &&
                    serverElement.ValueKind == JsonValueKind.Object)
                    server = serverElement.TryGetProperty("Version", out var version) ? version.GetString() : null;
            }
            catch { /* unparseable */ }
        }

        var error = server is null
            ? exitCode == 0
                ? "Docker client is installed but the engine is not reachable (is Docker Desktop running?)."
                : $"docker is not available: {stderr.Trim()}"
            : null;

        return (client, server, error);
    }

    private async Task<object?> EngineErrorAsync()
    {
        var (_, server, error) = await EngineAsync();
        return server is null ? new { available = false, error } : null;
    }

    private static Task<(int exitCode, bool timedOut, string stdout, string stderr, TimeSpan elapsed)>
        RunAsync(string args, int timeoutSeconds) =>
        ProcessRunner.RunAsync("docker", args, Directory.GetCurrentDirectory(), timeoutSeconds);

    /// <summary>A container name or image reference never starts with '-': that is a flag.</summary>
    private static bool LooksLikeFlag(string value) => value.TrimStart().StartsWith('-');

    /// <summary>Wraps a value in double quotes, escaping what the Windows and Unix parsers both accept.</summary>
    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static List<T> ReadJsonLines<T>(string stdout, Func<JsonElement, T> map)
    {
        var items = new List<T>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line.Trim());
                items.Add(map(document.RootElement));
            }
            catch { /* skip non-JSON line */ }
        }

        return items;
    }

    private static string Pretty(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ProcessRunner.Truncate(
                JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }), 32_000);
        }
        catch
        {
            return ProcessRunner.Truncate(json, 32_000);
        }
    }

    private static string? Get(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string? Prop(JsonElement element, string obj, string name) =>
        element.TryGetProperty(obj, out var child) && child.ValueKind == JsonValueKind.Object &&
        child.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;
}
