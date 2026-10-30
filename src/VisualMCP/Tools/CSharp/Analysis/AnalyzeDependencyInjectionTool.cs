using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class AnalyzeDependencyInjectionTool
{
    [McpServerTool, Description(
        "Analyse the Dependency Injection (DI) configuration in a .NET solution: " +
        "detect Scoped-inside-Singleton lifetime violations (captive dependencies), " +
        "services registered multiple times under the same interface, " +
        "services that are never resolved (registered but never injected), " +
        "constructors with too many dependencies (god-class smell), and " +
        "direct use of IServiceProvider.GetService/GetRequiredService in non-factory code " +
        "(service-locator anti-pattern). " +
        "Works with Microsoft.Extensions.DependencyInjection patterns " +
        "(AddSingleton/AddScoped/AddTransient/AddHostedService). " +
        "Requires load_solution first.")]
    public static Task<object> AnalyzeDependencyInjection(
        IAnalyzeDependencyInjectionService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Maximum number of constructor parameters before flagging as too many (default: 7)")] int maxConstructorParams = 7)
        => svc.AnalyzeDependencyInjection(projectName, maxConstructorParams);
}