using Microsoft.CodeAnalysis;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>Why a symbol is called by a framework rather than by in-solution code.</summary>
public sealed record FrameworkEntryPoint(string Framework, string Reason);

/// <summary>
/// Recognises the members a framework invokes by convention — MVC actions, Razor Pages handlers,
/// SignalR hub methods, hosted services, Blazor lifecycle methods and parameters, EF Core overrides and
/// DbSets, middleware, test methods, [JSInvokable], [Inject] — so "no reference in source" is not
/// reported as "unused" when routing, dependency injection or a test runner is the caller.
/// </summary>
/// <remarks>
/// The rules match by namespace and type name rather than by assembly identity: a project that does not
/// reference ASP.NET Core still gets the right answer for a type declared in that namespace, and the
/// classifier stays free of a framework dependency.
/// </remarks>
public static class FrameworkInvocation
{
    private static readonly string[] TestNamespaces =
    [
        "Xunit", "NUnit.Framework", "Microsoft.VisualStudio.TestTools.UnitTesting",
    ];

    private static readonly string[] LifecycleMethodNames =
    [
        "OnInitialized", "OnInitializedAsync", "OnParametersSet", "OnParametersSetAsync",
        "ShouldRender", "OnAfterRender", "OnAfterRenderAsync", "SetParametersAsync",
    ];

    /// <summary>The framework that invokes this symbol, or null when no convention applies.</summary>
    public static FrameworkEntryPoint? Classify(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared) return null;

        foreach (var attribute in symbol.GetAttributes())
        {
            var attributeType = attribute.AttributeClass;
            if (attributeType is null) continue;

            var attributeNamespace = attributeType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var attributeName = attributeType.Name;

            if (attributeNamespace.StartsWith("Microsoft.AspNetCore.Mvc", StringComparison.Ordinal))
                return new FrameworkEntryPoint("ASP.NET Core MVC", $"[{attributeName}]");
            if (attributeNamespace.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal))
                return new FrameworkEntryPoint("SignalR", $"[{attributeName}]");
            if (attributeNamespace == "Microsoft.JSInterop" && attributeName == "JSInvokableAttribute")
                return new FrameworkEntryPoint("Blazor", "[JSInvokable]");
            if (attributeName is "ParameterAttribute" or "CascadingParameterAttribute")
                return new FrameworkEntryPoint("Blazor", $"[{attributeName}]");
            if (attributeName == "InjectAttribute")
                return new FrameworkEntryPoint("Dependency injection", "[Inject]");
            if (TestNamespaces.Any(ns => attributeNamespace.Equals(ns, StringComparison.Ordinal) ||
                                         attributeNamespace.StartsWith(ns + ".", StringComparison.Ordinal)))
                return new FrameworkEntryPoint("Test runner", $"[{attributeName}]");
        }

        var containingType = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (containingType is null) return null;

        var framework = FrameworkOfType(containingType);
        if (framework is null) return null;

        return symbol switch
        {
            INamedTypeSymbol => new FrameworkEntryPoint(framework,
                $"{framework} loads this type by convention"),
            IMethodSymbol method => MethodEntryPoint(framework, method),
            IPropertySymbol property => PropertyEntryPoint(framework, property),
            _ => null,
        };
    }

    private static FrameworkEntryPoint? MethodEntryPoint(string framework, IMethodSymbol method)
    {
        if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation))
            return null;
        if (method.IsStatic) return null;

        var name = method.Name;

        return framework switch
        {
            "ASP.NET Core MVC" => IsPublic(method)
                ? new FrameworkEntryPoint(framework, "an MVC action is routed to by name")
                : null,
            "SignalR" => IsPublic(method)
                ? new FrameworkEntryPoint(framework, "a public hub method is invoked by the client")
                : null,
            "Razor Pages" => IsPublic(method) && name.StartsWith("On", StringComparison.Ordinal) &&
                             (name.Contains("Get", StringComparison.Ordinal) ||
                              name.Contains("Post", StringComparison.Ordinal) ||
                              name.Contains("Put", StringComparison.Ordinal) ||
                              name.Contains("Delete", StringComparison.Ordinal) ||
                              name.Contains("Patch", StringComparison.Ordinal) ||
                              name.Contains("Head", StringComparison.Ordinal))
                ? new FrameworkEntryPoint(framework, "a Razor Pages handler is invoked by name")
                : null,
            "Blazor" when LifecycleMethodNames.Contains(name) =>
                new FrameworkEntryPoint(framework, "a component lifecycle method"),
            "Hosted service" when name is "ExecuteAsync" or "StartAsync" or "StopAsync" =>
                new FrameworkEntryPoint(framework, "the host calls this method"),
            "EF Core" when name is "OnModelCreating" or "OnConfiguring" or "Configure" =>
                new FrameworkEntryPoint(framework, "EF Core calls this method"),
            "Middleware" when name is "Invoke" or "InvokeAsync" =>
                new FrameworkEntryPoint(framework, "the request pipeline calls this method"),
            _ => null,
        };
    }

    private static FrameworkEntryPoint? PropertyEntryPoint(string framework, IPropertySymbol property) =>
        framework switch
        {
            "EF Core" when property.Type.Name.StartsWith("DbSet", StringComparison.Ordinal) =>
                new FrameworkEntryPoint(framework, "a DbSet property is read by the model"),
            "Blazor" when IsPublic(property) =>
                new FrameworkEntryPoint(framework, "a public component property is set by the renderer"),
            _ => null,
        };

    private static bool IsPublic(ISymbol symbol) =>
        symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

    /// <summary>The framework whose base class, interface or middleware shape this type fits.</summary>
    public static string? FrameworkOfType(INamedTypeSymbol type)
    {
        // From the base type: the framework's own ControllerBase/Hub/ComponentBase is not an entry point.
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            var ns = current.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var name = current.Name;

            if (ns == "Microsoft.AspNetCore.Mvc" && name is "Controller" or "ControllerBase")
                return "ASP.NET Core MVC";
            if (ns == "Microsoft.AspNetCore.SignalR" && name == "Hub")
                return "SignalR";
            if (ns == "Microsoft.AspNetCore.Mvc.RazorPages" && name == "PageModel")
                return "Razor Pages";
            if (ns == "Microsoft.AspNetCore.Components" && name == "ComponentBase")
                return "Blazor";
            if (ns == "Microsoft.EntityFrameworkCore" && name == "DbContext")
                return "EF Core";
            if (name == "BackgroundService" && ns.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal))
                return "Hosted service";

            foreach (var @interface in current.AllInterfaces)
            {
                var interfaceNamespace = @interface.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                if (@interface.Name == "IHostedService" &&
                    interfaceNamespace.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal))
                    return "Hosted service";
                if (@interface.Name == "IEntityTypeConfiguration" &&
                    interfaceNamespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
                    return "EF Core";
            }
        }

        if (LooksLikeMiddleware(type)) return "Middleware";
        return null;
    }

    /// <summary>The ASP.NET Core middleware shape: a constructor taking the next delegate and an Invoke method.</summary>
    private static bool LooksLikeMiddleware(INamedTypeSymbol type)
    {
        var hasNextDelegate = type.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length > 0 &&
            constructor.Parameters[constructor.Parameters.Length - 1].Type.Name == "RequestDelegate");
        if (!hasNextDelegate) return false;

        return type.GetMembers("InvokeAsync").OfType<IMethodSymbol>().Any() ||
               type.GetMembers("Invoke").OfType<IMethodSymbol>().Any();
    }
}
