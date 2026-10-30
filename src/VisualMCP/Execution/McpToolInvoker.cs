using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace VisualMCP.Execution;

/// <summary>
/// Calls this server's own tools in-process, by the name <c>tools/list</c> advertises.
///
/// It binds arguments exactly the way the MCP SDK binds them for a client call — a parameter the
/// container can satisfy is resolved from DI and left out of the arguments; every other parameter is
/// deserialised from the matching JSON property with web defaults (camelCase, case-insensitive) — so a
/// plan sees the same tool surface, the same names and the same defaults a model would. That is what
/// lets <c>run_plan</c> fan out over the whole catalogue without a hand-maintained mapping that would
/// drift the first time a tool gains a parameter.
/// </summary>
public sealed class McpToolInvoker : IPlanToolInvoker
{
    /// <summary>
    /// Tools a plan may not call: the orchestrator itself (recursion), and the two calls that would take
    /// the server down mid-plan. Refusing them by name keeps the failure in the plan's report.
    /// </summary>
    private static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_plan", "RunPlan", "stop_server", "StopServer", "self_update", "SelfUpdate",
    };

    private static readonly Lazy<IReadOnlyList<ToolBinding>> Catalog = new(Discover, isThreadSafe: true);

    private static readonly JsonSerializerOptions Binding =
        new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _services;
    private readonly IServiceProviderIsService? _isService;
    private readonly Dictionary<string, ToolBinding> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ToolBinding, bool[]> _fromServices = [];
    private readonly Dictionary<Type, object> _instances = [];
    private readonly List<string> _names = [];

    public McpToolInvoker(IServiceProvider services)
    {
        _services = services;
        _isService = services.GetService<IServiceProviderIsService>();

        foreach (var binding in Catalog.Value)
        {
            // Which parameters the container satisfies is a per-host fact, not a per-method one: the SDK
            // asks the same question when it builds a tool's JSON schema, and a plan has to agree with it.
            var fromServices = new bool[binding.Parameters.Length];
            for (var i = 0; i < binding.Parameters.Length; i++)
            {
                var kind = binding.Parameters[i].ParameterType;
                fromServices[i] = kind != typeof(CancellationToken)
                                  && kind != typeof(IServiceProvider)
                                  && (_isService?.IsService(kind) ?? services.GetService(kind) is not null);
            }

            _fromServices[binding] = fromServices;

            if (_byName.TryAdd(binding.Name, binding))
                _names.Add(binding.Name);

            foreach (var alias in binding.Aliases)
            {
                if (_byName.TryAdd(alias, binding))
                    _names.Add(alias);
            }
        }
    }

    public IReadOnlyCollection<string> Names => _names;

    public async Task<object?> InvokeAsync(string name, JsonElement? arguments, CancellationToken ct)
    {
        if (!_byName.TryGetValue(name, out var binding))
            throw new PlanStepException($"there is no tool called '{name}'.");

        if (Refused.Contains(binding.Name) || Refused.Contains(binding.Method.Name))
            throw new PlanStepException($"a plan may not call '{binding.Name}'.");

        var values = new object?[binding.Parameters.Length];
        for (var i = 0; i < binding.Parameters.Length; i++)
        {
            var parameter = binding.Parameters[i];
            var type = parameter.ParameterType;

            if (type == typeof(CancellationToken))
            {
                values[i] = ct;
            }
            else if (type == typeof(IServiceProvider))
            {
                values[i] = _services;
            }
            else if (_fromServices[binding][i])
            {
                values[i] = Resolve(type);
            }
            else if (TryRead(arguments, parameter.Name, out var element))
            {
                values[i] = element.Deserialize(type, Binding)
                            ?? throw new PlanStepException(
                                $"argument '{parameter.Name}' of '{binding.Name}' was null, which it does not accept.");
            }
            else if (parameter.HasDefaultValue)
            {
                values[i] = parameter.DefaultValue;
            }
            else
            {
                throw new PlanStepException(
                    $"'{binding.Name}' needs the argument '{parameter.Name}' ({type.Name}), and the step did not pass it.");
            }
        }

        try
        {
            var target = binding.Method.IsStatic ? null : Instance(binding.Type);
            return await UnwrapAsync(binding.Method.Invoke(target, values)).ConfigureAwait(false);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable: the line above rethrows.
        }
    }

    private object Resolve(Type type) =>
        _services.GetService(type)
        ?? throw new PlanStepException($"the service {type.Name} is not registered in this host.");

    private object Instance(Type type)
    {
        if (_instances.TryGetValue(type, out var existing))
            return existing;

        var created = ActivatorUtilities.CreateInstance(_services, type);
        _instances[type] = created;
        return created;
    }

    private static bool TryRead(JsonElement? arguments, string? name, out JsonElement value)
    {
        value = default;
        if (arguments is not { ValueKind: JsonValueKind.Object } json || name is null)
            return false;

        if (json.TryGetProperty(name, out value))
            return true;

        foreach (var property in json.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static async Task<object?> UnwrapAsync(object? returned)
    {
        switch (returned)
        {
            case null:
                return null;

            case Task task:
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                return taskType.IsGenericType ? taskType.GetProperty("Result")?.GetValue(task) : null;

            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;

            case var other when other.GetType() is { IsGenericType: true } type
                                && type.GetGenericTypeDefinition() == typeof(ValueTask<>):
                var asTask = (Task)type.GetMethod("AsTask")!.Invoke(other, null)!;
                await asTask.ConfigureAwait(false);
                return asTask.GetType().GetProperty("Result")?.GetValue(asTask);

            default:
                return returned;
        }
    }

    /// <summary>
    /// Reads every <c>[McpServerTool]</c> method on every <c>[McpServerToolType]</c> class in this assembly.
    /// The name mirrors the SDK's rule: the attribute's name when set, the method name otherwise; a
    /// snake_case alias is added so a caller can write the spelling the documentation uses.
    /// </summary>
    private static IReadOnlyList<ToolBinding> Discover()
    {
        var bindings = new List<ToolBinding>();

        foreach (var type in typeof(McpToolInvoker).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute is null)
                    continue;

                var declared = string.IsNullOrWhiteSpace(attribute.Name) ? method.Name : attribute.Name!;
                var parameters = method.GetParameters();

                if (parameters.Any(p => p.GetCustomAttribute<FromKeyedServicesAttribute>() is not null))
                    continue; // no tool here uses keyed services, and a plan cannot bind them either.

                bindings.Add(new ToolBinding(
                    declared,
                    Aliases(method.Name, declared),
                    method,
                    type,
                    parameters));
            }
        }

        return bindings;
    }

    private static string[] Aliases(string methodName, string declared)
    {
        var snake = ToSnakeCase(methodName);
        return string.Equals(snake, declared, StringComparison.Ordinal)
            ? []
            : [snake];
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    builder.Append('_');

                builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>One tool method and the parameters a caller may pass it.</summary>
    private sealed class ToolBinding(
        string name,
        string[] aliases,
        MethodInfo method,
        Type type,
        ParameterInfo[] parameters)
    {
        public string Name { get; } = name;
        public string[] Aliases { get; } = aliases;
        public MethodInfo Method { get; } = method;
        public Type Type { get; } = type;
        public ParameterInfo[] Parameters { get; } = parameters;
    }
}
