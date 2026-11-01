using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VisualMCP.Install;

/// <summary>
/// The installer's configuration edit, run from the published server binary with
/// <c>--config &lt;mode&gt; …</c>. It replaces the small Python program the POSIX installer used to
/// need: one JSON document is edited in place (every key that is not ours survives), or the routing
/// block is written into an AGENTS.md, so installing VisualMCP needs nothing but the .NET SDK.
/// </summary>
public static class ConfigEditor
{
    private const string Server = "VisualMCP";
    private const string BlockStatus = "VisualMCP: checking for a shell probe...";

    /// <summary>
    /// The tools the guard watches: Bash for the shell probes, Write/Edit for a memory store or a new
    /// plan-like .md. Claude Code and ZCode treat the matcher as a regex over the tool name, and the
    /// Windows installer registers the same one.
    /// </summary>
    private const string Matcher = "Bash|Write|Edit|MultiEdit|NotebookEdit";

    /// <summary>A hook this product registered, whatever spelling an earlier version used.</summary>
    private static readonly Regex Ours = new(
        @"pre-tool-use\.(py|ps1)|--hook\s+pre-tool-use",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Marker = new(
        @"\r?\n?<!-- VisualMCP:begin.*?<!-- VisualMCP:end -->\r?\n?",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Runs one mode; returns the process exit code (0 on success, 2 on a refused edit).</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2) return Refuse("usage: --config <mode> <path> [value] [value]");
        var mode = args[0];
        var path = args[1];
        var first = args.Length > 2 ? args[2] : null;
        var second = args.Length > 3 ? args[3] : null;

        try
        {
            switch (mode)
            {
                case "zcode-set":
                {
                    var config = Load(path);
                    var servers = Child(Child(config, "mcp"), "servers");
                    servers[Server] = ServerEntry(first, second);
                    Save(path, config);
                    return 0;
                }
                case "zcode-remove":
                {
                    var config = Load(path);
                    if (config["mcp"] is JsonObject mcp && mcp["servers"] is JsonObject servers &&
                        servers.Remove(Server))
                        Save(path, config);
                    return 0;
                }
                case "mcpservers-set":
                {
                    var config = Load(path);
                    Child(config, "mcpServers")[Server] = ServerEntry(first, second);
                    Save(path, config);
                    return 0;
                }
                case "mcpservers-remove":
                {
                    var config = Load(path);
                    if (config["mcpServers"] is JsonObject servers && servers.Remove(Server))
                        Save(path, config);
                    return 0;
                }
                case "zcode-hook-set":
                {
                    var config = Load(path);
                    var hooks = Child(config, "hooks");
                    hooks["enabled"] = true;
                    var events = Child(hooks, "events");
                    events["PreToolUse"] = Matchers(events["PreToolUse"], first);
                    Save(path, config);
                    return 0;
                }
                case "zcode-hook-remove":
                {
                    var config = Load(path);
                    if (config["hooks"] is JsonObject hooks)
                    {
                        if (hooks["events"] is JsonObject events && events.ContainsKey("PreToolUse"))
                        {
                            var kept = KeepForeign(events["PreToolUse"]);
                            if (kept.Count > 0) events["PreToolUse"] = kept;
                            else events.Remove("PreToolUse");
                            if (events.Count == 0) hooks.Remove("events");
                        }
                        if (hooks.Count == 0) config.Remove("hooks");
                        Save(path, config);
                    }
                    return 0;
                }
                case "claude-hook-set":
                {
                    var config = Load(path);
                    var hooks = Child(config, "hooks");
                    hooks["PreToolUse"] = Matchers(hooks["PreToolUse"], first);
                    Save(path, config);
                    return 0;
                }
                case "claude-hook-remove":
                {
                    var config = Load(path);
                    if (config["hooks"] is JsonObject hooks && hooks.ContainsKey("PreToolUse"))
                    {
                        var kept = KeepForeign(hooks["PreToolUse"]);
                        if (kept.Count > 0) hooks["PreToolUse"] = kept;
                        else hooks.Remove("PreToolUse");
                        if (hooks.Count == 0) config.Remove("hooks");
                        Save(path, config);
                    }
                    return 0;
                }
                case "agents-block-set":
                    return WriteAgentsBlock(path, first);
                case "agents-block-remove":
                    return RemoveAgentsBlock(path);
                default:
                    return Refuse($"unknown mode '{mode}'");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             JsonException or InvalidOperationException)
        {
            return Refuse(exception.Message);
        }
    }

    // ── JSON ────────────────────────────────────────────────────────────────

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var raw = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();

        var node = JsonNode.Parse(raw, nodeOptions: null, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return node as JsonObject ?? throw new InvalidOperationException($"{path} is not a JSON object");
    }

    private static void Save(string path, JsonObject config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory ?? ".", ".visualmcp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temporary, config.ToJsonString(SaveOptions) + "\n", new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            throw;
        }
    }

    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonObject ServerEntry(string? command, string? argument)
    {
        var entry = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = command,
            ["args"] = new JsonArray(),
            ["timeoutMs"] = 60000,
        };
        if (!string.IsNullOrEmpty(argument)) entry["args"] = new JsonArray(argument);
        return entry;
    }

    private static JsonObject HookEntry(string? command) => new()
    {
        ["type"] = "command",
        ["command"] = command,
        ["timeout"] = 15,
        ["statusMessage"] = BlockStatus,
    };

    /// <summary>The entries that are not this product's, then one matcher of ours (last, as before).</summary>
    private static JsonArray Matchers(JsonNode? existing, string? command)
    {
        var kept = KeepForeign(existing);
        kept.Add(new JsonObject
        {
            ["matcher"] = Matcher,
            ["hooks"] = new JsonArray(HookEntry(command)),
        });
        return kept;
    }

    private static JsonArray KeepForeign(JsonNode? entries)
    {
        var kept = new JsonArray();
        if (entries is not JsonArray array) return kept;

        foreach (var entry in array)
        {
            if (entry is null || IsOurs(entry)) continue;
            kept.Add(entry.DeepClone());
        }
        return kept;
    }

    private static bool IsOurs(JsonNode matcher)
    {
        if (matcher is not JsonObject entry || entry["hooks"] is not JsonArray hooks) return false;
        foreach (var hook in hooks)
        {
            if (hook is JsonObject item && item["command"] is JsonValue value &&
                value.TryGetValue<string>(out var command) && Ours.IsMatch(command))
                return true;
        }
        return false;
    }

    // ── AGENTS.md ───────────────────────────────────────────────────────────

    private static int WriteAgentsBlock(string path, string? blockSource)
    {
        if (string.IsNullOrWhiteSpace(blockSource) || !File.Exists(blockSource))
            return Refuse("the AGENTS.md block source was not found");

        var cleaned = StripMarker(path);
        var block = File.ReadAllText(blockSource).Replace("\r\n", "\n").Trim();
        var content = cleaned.Length > 0 ? cleaned + "\n\n" + block + "\n" : block + "\n";

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return 0;
    }

    private static int RemoveAgentsBlock(string path)
    {
        var cleaned = StripMarker(path);
        if (cleaned.Length == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return 0;
        }

        File.WriteAllText(path, cleaned + "\n", new UTF8Encoding(false));
        return 0;
    }

    private static string StripMarker(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        return Marker.Replace(text, string.Empty).Trim();
    }

    private static int Refuse(string message)
    {
        Console.Error.WriteLine("VisualMCP config: " + message);
        return 2;
    }
}
