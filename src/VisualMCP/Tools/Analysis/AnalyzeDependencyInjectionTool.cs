using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Analysis;

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
    public static async Task<object> AnalyzeDependencyInjection(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Maximum number of constructor parameters before flagging as too many (default: 7)")] int maxConstructorParams = 7)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var registrations  = new List<ServiceRegistration>();
        var injections     = new List<ServiceInjection>();
        var findings       = new List<object>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root  = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                CollectRegistrations(root, model, document, project, registrations);
                CollectInjections(root, model, document, project, injections);
                CheckServiceLocatorUsage(root, model, document, project, findings);
                CheckOverloadedConstructors(root, model, document, project, findings, maxConstructorParams);
            }
        }

        // ── Lifetime violations (Scoped/Transient inside Singleton) ──────────────
        CheckLifetimeViolations(registrations, injections, findings);

        // ── Duplicate registrations ──────────────────────────────────────────────
        CheckDuplicateRegistrations(registrations, findings);

        // ── Services registered but never resolved ───────────────────────────────
        CheckUnresolvedServices(registrations, injections, findings);

        var byKind = findings
            .GroupBy(f => ((dynamic)f).IssueKind.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            projectFilter         = projectName ?? "all",
            maxConstructorParams,
            registrationCount     = registrations.Count,
            totalFindings         = findings.Count,
            byKind,
            registrationSummary   = registrations
                .GroupBy(r => r.Lifetime)
                .ToDictionary(g => g.Key, g => g.Count()),
            findings,
        };
    }

    // ── Collection ───────────────────────────────────────────────────────────────

    private static void CollectRegistrations(
        SyntaxNode root, SemanticModel model, Document doc, Project project,
        List<ServiceRegistration> registrations)
    {
        var lifetimeMethods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AddSingleton"]    = "Singleton",
            ["AddScoped"]       = "Scoped",
            ["AddTransient"]    = "Transient",
            ["AddHostedService"]= "Singleton",
            ["TryAddSingleton"] = "Singleton",
            ["TryAddScoped"]    = "Scoped",
            ["TryAddTransient"] = "Transient",
        };

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? methodName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _                                => null,
            };

            if (methodName is null || !lifetimeMethods.TryGetValue(methodName, out var lifetime)) continue;

            var sym = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (sym is null) continue;

            var containingNs = sym.ContainingNamespace?.ToDisplayString() ?? "";
            if (!containingNs.Contains("DependencyInjection") &&
                !containingNs.Contains("Extensions")) continue;

            // Extract service type from generic arguments
            var genericName = (invocation.Expression as MemberAccessExpressionSyntax)?.Name as GenericNameSyntax;
            var typeArgs     = genericName?.TypeArgumentList.Arguments;

            var serviceType       = typeArgs?.ElementAtOrDefault(0)?.ToString() ?? "?";
            var implementationType = typeArgs?.ElementAtOrDefault(1)?.ToString() ?? serviceType;

            var loc = invocation.GetLocation().GetLineSpan();
            registrations.Add(new ServiceRegistration(
                ServiceType: serviceType,
                ImplementationType: implementationType,
                Lifetime: lifetime,
                FilePath: doc.FilePath ?? "",
                Line: loc.StartLinePosition.Line + 1,
                Project: project.Name));
        }
    }

    private static void CollectInjections(
        SyntaxNode root, SemanticModel model, Document doc, Project project,
        List<ServiceInjection> injections)
    {
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            var ctorSym = model.GetDeclaredSymbol(ctor) as IMethodSymbol;
            if (ctorSym is null) continue;

            var containingType = ctorSym.ContainingType;
            var loc = ctor.GetLocation().GetLineSpan();

            foreach (var param in ctor.ParameterList.Parameters)
            {
                var paramType = model.GetTypeInfo(param.Type!).Type?.Name ?? param.Type?.ToString() ?? "?";
                injections.Add(new ServiceInjection(
                    ConsumerType: containingType.Name,
                    InjectedType: paramType,
                    FilePath: doc.FilePath ?? "",
                    Line: loc.StartLinePosition.Line + 1,
                    Project: project.Name));
            }
        }
    }

    // ── Checks ───────────────────────────────────────────────────────────────────

    private static void CheckLifetimeViolations(
        List<ServiceRegistration> registrations, List<ServiceInjection> injections, List<object> findings)
    {
        // Build a quick lookup: type name -> lifetime
        var lifetimeMap = registrations
            .GroupBy(r => SimpleName(r.ServiceType))
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => LifetimeRank(r.Lifetime)).First().Lifetime);

        // For each singleton registration, check if its constructor injects Scoped/Transient services
        foreach (var singleton in registrations.Where(r => r.Lifetime == "Singleton"))
        {
            var implName = SimpleName(singleton.ImplementationType);
            var injected = injections.Where(i => i.ConsumerType == implName);

            foreach (var dep in injected)
            {
                if (!lifetimeMap.TryGetValue(SimpleName(dep.InjectedType), out var depLifetime)) continue;
                if (depLifetime is "Scoped" or "Transient")
                {
                    findings.Add(new
                    {
                        IssueKind   = "Lifetime violation (captive dependency)",
                        Severity    = "Error",
                        Detail      = $"Singleton '{singleton.ServiceType}' depends on {depLifetime} '{dep.InjectedType}'. " +
                                      $"The {depLifetime} service will be captured and behave as a Singleton.",
                        Project     = singleton.Project,
                        FilePath    = singleton.FilePath,
                        Line        = singleton.Line,
                    });
                }
            }
        }
    }

    private static void CheckDuplicateRegistrations(
        List<ServiceRegistration> registrations, List<object> findings)
    {
        var groups = registrations
            .GroupBy(r => $"{r.ServiceType}|{r.Lifetime}")
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var first = group.First();
            findings.Add(new
            {
                IssueKind   = "Duplicate registration",
                Severity    = "Warning",
                Detail      = $"'{first.ServiceType}' is registered as {first.Lifetime} {group.Count()} times. " +
                              "Only the last registration wins; earlier ones are shadowed.",
                Project     = first.Project,
                FilePath    = first.FilePath,
                Line        = first.Line,
                Occurrences = group.Select(r => new { r.FilePath, r.Line }).ToList(),
            });
        }
    }

    private static void CheckUnresolvedServices(
        List<ServiceRegistration> registrations, List<ServiceInjection> injections, List<object> findings)
    {
        var injectedTypes = injections
            .Select(i => SimpleName(i.InjectedType))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var reg in registrations)
        {
            var name = SimpleName(reg.ServiceType);
            // Skip well-known framework types and hosted services
            if (name is "IHostedService" or "BackgroundService" or "IStartupFilter") continue;
            if (name.StartsWith("I", StringComparison.Ordinal) == false) continue; // only check interfaces

            if (!injectedTypes.Contains(name))
            {
                findings.Add(new
                {
                    IssueKind = "Registered but never resolved",
                    Severity  = "Info",
                    Detail    = $"'{reg.ServiceType}' is registered as {reg.Lifetime} but no constructor injects it. " +
                                "It may be resolved dynamically (IServiceProvider) or may be dead code.",
                    Project   = reg.Project,
                    FilePath  = reg.FilePath,
                    Line      = reg.Line,
                });
            }
        }
    }

    private static void CheckServiceLocatorUsage(
        SyntaxNode root, SemanticModel model, Document doc, Project project, List<object> findings)
    {
        var serviceLocatorMethods = new HashSet<string>
        {
            "GetService", "GetRequiredService", "GetServices",
        };

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;

            var methodName = ma.Name.Identifier.Text;
            if (!serviceLocatorMethods.Contains(methodName)) continue;

            var sym = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (sym is null) continue;

            var ns = sym.ContainingNamespace?.ToDisplayString() ?? "";
            var containingType = sym.ContainingType?.Name ?? "";
            if (!ns.Contains("DependencyInjection") && containingType != "IServiceProvider") continue;

            // Allow inside factory methods (methods named Create* or Build*)
            var enclosingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var enclosingName   = enclosingMethod?.Identifier.Text ?? "";
            if (enclosingName.StartsWith("Create", StringComparison.OrdinalIgnoreCase) ||
                enclosingName.StartsWith("Build",  StringComparison.OrdinalIgnoreCase) ||
                enclosingName == "ConfigureServices") continue;

            var loc = invocation.GetLocation().GetLineSpan();
            findings.Add(new
            {
                IssueKind = "Service Locator anti-pattern",
                Severity  = "Warning",
                Detail    = $"'{methodName}' called outside a factory method. " +
                            "Prefer constructor injection — it makes dependencies explicit and testable.",
                Project   = project.Name,
                FilePath  = doc.FilePath,
                Line      = loc.StartLinePosition.Line + 1,
            });
        }
    }

    private static void CheckOverloadedConstructors(
        SyntaxNode root, SemanticModel model, Document doc, Project project,
        List<object> findings, int maxParams)
    {
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            var paramCount = ctor.ParameterList.Parameters.Count;
            if (paramCount <= maxParams) continue;

            var ctorSym = model.GetDeclaredSymbol(ctor) as IMethodSymbol;
            if (ctorSym is null) continue;

            var loc = ctor.GetLocation().GetLineSpan();
            findings.Add(new
            {
                IssueKind = "Constructor over-injection",
                Severity  = "Warning",
                Detail    = $"'{ctorSym.ContainingType.Name}' constructor has {paramCount} parameters (>{maxParams}). " +
                            "Consider splitting responsibilities or introducing a Facade/aggregate service.",
                Project   = project.Name,
                FilePath  = doc.FilePath,
                Line      = loc.StartLinePosition.Line + 1,
            });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string SimpleName(string fullType)
    {
        var idx = fullType.LastIndexOf('.');
        var name = idx >= 0 ? fullType[(idx + 1)..] : fullType;
        // Strip generic arity: IRepository`1 -> IRepository
        var backtick = name.IndexOf('`');
        return backtick >= 0 ? name[..backtick] : name;
    }

    private static int LifetimeRank(string lifetime) => lifetime switch
    {
        "Singleton"  => 0,
        "Scoped"     => 1,
        "Transient"  => 2,
        _            => 3,
    };

    private record ServiceRegistration(
        string ServiceType, string ImplementationType, string Lifetime,
        string FilePath, int Line, string Project);

    private record ServiceInjection(
        string ConsumerType, string InjectedType,
        string FilePath, int Line, string Project);
}
