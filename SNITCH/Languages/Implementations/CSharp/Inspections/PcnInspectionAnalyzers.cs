using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

// These analyzers are compiled into the server and run in-process through
// Compilation.WithAnalyzers, exactly like the house rules; the analyzer-package
// packaging rules (RS1036/RS1038/RS1041/RS2008) do not apply.
#pragma warning disable RS1036, RS1038, RS1041, RS2008

namespace Snitch.Languages.CSharp.Inspections.Pcn;

/// <summary>
/// The predictive-coding invariants, as Roslyn analyzers. They are deliberately scoped: a rule only
/// runs on a type that carries a PCN signature (precision, error, activity and weight members, or a
/// PCN/PredictiveCoding name), so the pack can be enabled for a solution without flagging unrelated
/// code that happens to contain a field named W or a method named Step.
/// </summary>
internal static class PcnRules
{
    public static DiagnosticDescriptor Rule(string id, string title, string message, DiagnosticSeverity severity) =>
        new(id, title, message, "PredictiveCoding", severity, isEnabledByDefault: true,
            description: "An invariant of the predictive-coding network: free-energy minimization with local Hebbian updates.");
}

/// <summary>Name and operation helpers shared by the four PCN rules.</summary>
internal static class PcnFacts
{
    private static readonly HashSet<string> PrecisionNames =
        new(StringComparer.OrdinalIgnoreCase) { "Pi", "Precision", "Prec", "PiHat", "Π" };

    private static readonly HashSet<string> ErrorNames =
        new(StringComparer.OrdinalIgnoreCase) { "E", "Error", "Residual", "Delta", "PredictionError", "VE" };

    private static readonly HashSet<string> ActivityNames =
        new(StringComparer.OrdinalIgnoreCase) { "A", "Activity", "Mu", "State", "Activation", "VA" };

    private static readonly HashSet<string> WeightNames =
        new(StringComparer.OrdinalIgnoreCase) { "W", "V", "Weight", "Weights", "Kernel", "Bias", "Theta", "Parameters" };

    private static readonly HashSet<string> LocalUpdateMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "UpdateWeights", "UpdateWeight", "Learn", "Hebbian", "UpdateParameters",
            "ApplyLocalUpdate", "ApplyUpdate", "Update",
        };

    /// <summary>A type that looks like a predictive-coding network or one of its layers.</summary>
    public static bool IsPcnType(INamedTypeSymbol? type)
    {
        if (type is null) return false;

        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (ContainsWord(current.Name, "PCN") ||
                ContainsWord(current.Name, "PredictiveCoding") ||
                ContainsWord(current.Name, "PredictiveCodingNetwork"))
                return true;

            var names = current.GetMembers()
                .Select(member => Normalize(member.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var signals = 0;
            if (names.Overlaps(PrecisionNames)) signals++;
            if (names.Overlaps(ErrorNames)) signals++;
            if (names.Overlaps(ActivityNames)) signals++;
            if (names.Overlaps(WeightNames)) signals++;
            if (names.Any(name => ContainsWord(name, "UpdateActivity") || ContainsWord(name, "UpdateWeights")))
                signals++;

            if (signals >= 3) return true;
        }

        return false;
    }

    public static bool HasMembers(INamedTypeSymbol type, params string[] names) =>
        type.GetMembers().Any(member => names.Any(name =>
            Normalize(member.Name).Equals(name, StringComparison.OrdinalIgnoreCase)));

    public static bool IsGatingMethod(IMethodSymbol method)
    {
        var type = method.ContainingType;
        var nameMatches =
            ContainsWord(method.Name, "Route") ||
            ContainsWord(method.Name, "Gate") ||
            ContainsWord(method.Name, "Gating") ||
            ContainsWord(method.Name, "SelectExpert") ||
            ContainsWord(method.Name, "SelectExperts") ||
            ContainsWord(method.Name, "Dispatch");

        var typeMatches = type is not null &&
            (ContainsWord(type.Name, "Router") ||
             ContainsWord(type.Name, "Gate") ||
             ContainsWord(type.Name, "Gating") ||
             ContainsWord(type.Name, "MoE") ||
             ContainsWord(type.Name, "MixtureOfExperts") ||
             ContainsWord(type.Name, "Expert"));

        if (!nameMatches && !typeMatches) return false;

        // A Router in a web application is not a mixture-of-experts router: require a PCN shape or
        // the two members the energy gate is defined over.
        return IsPcnType(type) ||
               (type is not null && HasMembers(type, "Beta", "F", "FreeEnergy", "Energy"));
    }

    public static bool IsActivityUpdateMethod(string name) =>
        ContainsWord(name, "UpdateActivity") ||
        ContainsWord(name, "UpdateActivities") ||
        ContainsWord(name, "UpdateState") ||
        ContainsWord(name, "UpdateMu") ||
        ContainsWord(name, "Advance") ||
        ContainsWord(name, "Integrate") ||
        name.Equals("Step", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Infer", StringComparison.OrdinalIgnoreCase);

    public static bool IsWeightName(string? name) => name is not null && WeightNames.Contains(name);

    public static bool IsErrorName(string? name) => name is not null && ErrorNames.Contains(name);

    public static bool IsActivityName(string? name) => name is not null && ActivityNames.Contains(name);

    public static bool IsPrecisionName(string? name) => name is not null && PrecisionNames.Contains(name);

    public static bool IsEtaName(string name) =>
        ContainsWord(name, "eta") ||
        ContainsWord(name, "learningRate") ||
        name.Equals("lr", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("rate", StringComparison.OrdinalIgnoreCase);

    public static bool IsInputName(string name) =>
        ContainsWord(name, "lower") ||
        ContainsWord(name, "upper") ||
        ContainsWord(name, "input") ||
        ContainsWord(name, "source") ||
        ContainsWord(name, "context") ||
        name.Equals("x", StringComparison.OrdinalIgnoreCase);

    public static bool IsPreviousName(string name) =>
        ContainsWord(name, "prev") ||
        ContainsWord(name, "previous") ||
        ContainsWord(name, "tMinus1") ||
        name.Equals("t_1", StringComparison.OrdinalIgnoreCase);

    public static bool IsBetaName(string name) =>
        ContainsWord(name, "beta") || name.Equals("β", StringComparison.OrdinalIgnoreCase);

    public static bool IsFreeEnergyName(string name) =>
        name.Equals("F", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("F_i", StringComparison.OrdinalIgnoreCase) ||
        ContainsWord(name, "freeEnergy") ||
        ContainsWord(name, "energy");

    public static bool IsLocalUpdateMethod(string name) => LocalUpdateMethods.Contains(Normalize(name));

    public static bool IsResidual(IOperation operation) => operation switch
    {
        IBinaryOperation binary => binary.OperatorKind == BinaryOperatorKind.Subtract,
        IInvocationOperation invocation => IsResidualMethod(invocation.TargetMethod.Name),
        _ => false,
    };

    public static bool IsHardSelection(string name) =>
        name.Equals("ArgMax", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Argmax", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Max", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MaxBy", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TopK", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Top", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OrderByDescending", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Sort", StringComparison.OrdinalIgnoreCase);

    public static bool ReferencesName(IOperation? root, Func<string, bool> predicate)
    {
        if (root is null) return false;

        foreach (var operation in root.DescendantsAndSelf())
        {
            var name = MemberName(operation);
            if (name is not null && predicate(name)) return true;
        }

        return false;
    }

    public static bool HasNegation(IOperation? root) =>
        root is not null && root.DescendantsAndSelf().Any(operation =>
            operation is IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus });

    public static string? MemberName(IOperation? operation)
    {
        operation = Unwrap(operation);

        return operation switch
        {
            IFieldReferenceOperation field => Normalize(field.Field.Name),
            IPropertyReferenceOperation property => Normalize(property.Property.Name),
            ILocalReferenceOperation local => Normalize(local.Local.Name),
            IParameterReferenceOperation parameter => Normalize(parameter.Parameter.Name),
            _ => null,
        };
    }

    public static IOperation? Unwrap(IOperation? operation) => operation switch
    {
        IArrayElementReferenceOperation element => Unwrap(element.ArrayReference),
        IPropertyReferenceOperation { Arguments.Length: > 0 } indexer => Unwrap(indexer.Instance),
        IConversionOperation conversion => Unwrap(conversion.Operand),
        IParenthesizedOperation parenthesized => Unwrap(parenthesized.Operand),
        _ => operation,
    };

    public static bool ContainsWord(string value, string fragment) =>
        value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string name) => name.TrimStart('_');

    private static bool IsResidualMethod(string name) =>
        name.Equals("Sub", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Subtract", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Minus", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Difference", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// PCN0001 — a prediction error stored by a layer must be precision-weighted: E = Π ⊙ (prediction − μ).
/// An error assigned from a bare residual is what a free-energy network must not do.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PrecisionWeightedErrorAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "PCN0001";

    private static readonly DiagnosticDescriptor Rule = PcnRules.Rule(
        RuleId,
        "Precision-weighted prediction error",
        "The prediction error '{0}' is not precision-weighted; multiply the residual by the precision (Π) before storing it",
        DiagnosticSeverity.Warning);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.MethodDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (method.Body is null && method.ExpressionBody is null) return;

        var symbol = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken);
        if (symbol is null || !PcnFacts.IsPcnType(symbol.ContainingType)) return;
        if (symbol.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor) return;

        var operation = context.SemanticModel.GetOperation(method, context.CancellationToken);
        if (operation is null) return;

        var operations = operation.DescendantsAndSelf().ToList();
        if (!operations.Any(PcnFacts.IsResidual)) return;

        var errorAssignments = operations
            .OfType<IAssignmentOperation>()
            .Where(assignment => PcnFacts.IsErrorName(PcnFacts.MemberName(assignment.Target)))
            .ToList();

        foreach (var assignment in errorAssignments)
        {
            if (PcnFacts.ReferencesName(assignment.Value, PcnFacts.IsPrecisionName)) continue;

            var ownsResidual = assignment.Value.DescendantsAndSelf().Any(PcnFacts.IsResidual);
            var methodHasPrecision = PcnFacts.ReferencesName(operation, PcnFacts.IsPrecisionName);

            if (ownsResidual || !methodHasPrecision)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    assignment.Syntax.GetLocation(),
                    PcnFacts.MemberName(assignment.Target) ?? symbol.Name));
                return;
            }
        }

        if (errorAssignments.Count == 0 &&
            PcnFacts.ContainsWord(symbol.Name, "Error") &&
            !PcnFacts.ReferencesName(operation, PcnFacts.IsPrecisionName))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), symbol.Name));
        }
    }
}

/// <summary>
/// PCN0002 — every weight change is a local Hebbian rule. A weight written outside the layer's own
/// update method, and any use of a global backpropagation/autodiff API, is reported.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LocalHebbianOnlyAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "PCN0002";

    private static readonly DiagnosticDescriptor Rule = PcnRules.Rule(
        RuleId,
        "Local Hebbian updates only",
        "Global backpropagation is not allowed: {0}",
        DiagnosticSeverity.Warning);

    private static readonly string[] BackpropNames =
        ["Backward", "Backprop", "Backpropagate", "BackwardPass", "ApplyGradients", "ComputeGradients"];

    private static readonly string[] AutodiffNamespaces =
        ["TorchSharp", "TensorFlow", "DiffSharp", "Microsoft.ML.Trainers", "Microsoft.ML.Data", "Microsoft.ML.Transforms"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeAssignment,
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxKind.AddAssignmentExpression,
            SyntaxKind.SubtractAssignmentExpression,
            SyntaxKind.MultiplyAssignmentExpression,
            SyntaxKind.DivideAssignmentExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeUsing, SyntaxKind.UsingDirective);
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        var enclosing = context.SemanticModel.GetEnclosingSymbol(assignment.SpanStart, context.CancellationToken) as IMethodSymbol;
        if (enclosing is null || !PcnFacts.IsPcnType(enclosing.ContainingType)) return;
        if (enclosing.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor) return;
        if (PcnFacts.IsLocalUpdateMethod(enclosing.Name)) return;

        var operation = context.SemanticModel.GetOperation(assignment, context.CancellationToken) as IAssignmentOperation;
        if (operation is null) return;

        var weight = PcnFacts.MemberName(operation.Target);
        if (!PcnFacts.IsWeightName(weight)) return;

        if (LooksHebbian(operation.Value)) return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            assignment.Left.GetLocation(),
            $"weight '{weight}' is changed outside a local Hebbian update (move it into UpdateWeights)"));
    }

    /// <summary>η · error ⊗ input: the local rule, recognisable even outside the conventional method name.</summary>
    private static bool LooksHebbian(IOperation value) =>
        PcnFacts.ReferencesName(value, PcnFacts.IsEtaName) &&
        PcnFacts.ReferencesName(value, PcnFacts.IsErrorName) &&
        PcnFacts.ReferencesName(value, PcnFacts.IsInputName);

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var operation = context.SemanticModel.GetOperation(invocation, context.CancellationToken) as IInvocationOperation;
        if (operation is null) return;

        var method = operation.TargetMethod;
        var backpropName = BackpropNames.Any(name => method.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var autodiffNamespace = IsAutodiffNamespace(method.ContainingNamespace?.ToDisplayString());
        if (!backpropName && !autodiffNamespace) return;

        var enclosing = context.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, context.CancellationToken) as IMethodSymbol;
        if (!autodiffNamespace && (enclosing is null || !PcnFacts.IsPcnType(enclosing.ContainingType))) return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            $"'{method.Name}' from '{method.ContainingNamespace?.ToDisplayString()}' is a global backpropagation API"));
    }

    private static void AnalyzeUsing(SyntaxNodeAnalysisContext context)
    {
        var directive = (UsingDirectiveSyntax)context.Node;
        var ns = directive.Name?.ToString();
        if (!IsAutodiffNamespace(ns)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, directive.GetLocation(),
            $"'using {ns};' imports a global backpropagation framework"));
    }

    private static bool IsAutodiffNamespace(string? ns) =>
        ns is not null && AutodiffNamespaces.Any(prefix =>
            ns.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            ns.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// PCN0003 — the activity update carries its previous state: Δμ = dt·γ·E + Π_t(μ − μ_prev).
/// A memoryless update is reported.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TemporalContinuityAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "PCN0003";

    private static readonly DiagnosticDescriptor Rule = PcnRules.Rule(
        RuleId,
        "Temporal continuity",
        "Activity update '{0}' has no temporal-continuity term; add Π_t(μ_l − μ_l^{t−1})",
        DiagnosticSeverity.Warning);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.MethodDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (method.Body is null && method.ExpressionBody is null) return;

        var symbol = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken);
        if (symbol is null || !PcnFacts.IsPcnType(symbol.ContainingType)) return;
        if (!PcnFacts.IsActivityUpdateMethod(symbol.Name)) return;

        var operation = context.SemanticModel.GetOperation(method, context.CancellationToken);
        if (operation is null) return;

        var writesActivity = operation.DescendantsAndSelf()
            .OfType<IAssignmentOperation>()
            .Any(assignment => PcnFacts.IsActivityName(PcnFacts.MemberName(assignment.Target)));

        if (!writesActivity) return;
        if (PcnFacts.ReferencesName(operation, PcnFacts.IsPreviousName)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), symbol.Name));
    }
}

/// <summary>
/// PCN0004 — mixture-of-experts routing is g_i = softmax(−β·F_i). A gating method that uses a hard
/// selection, or a softmax whose argument is not −β·F, is reported.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EnergyBasedGatingAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "PCN0004";

    private static readonly DiagnosticDescriptor Rule = PcnRules.Rule(
        RuleId,
        "Energy-based gating",
        "MoE routing must be g_i = softmax(−β·F_i): {0}",
        DiagnosticSeverity.Warning);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.MethodDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (method.Body is null && method.ExpressionBody is null) return;

        var symbol = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken);
        if (symbol is null || !PcnFacts.IsGatingMethod(symbol)) return;

        var operation = context.SemanticModel.GetOperation(method, context.CancellationToken);
        if (operation is null) return;

        var softmaxCalls = operation.DescendantsAndSelf()
            .OfType<IInvocationOperation>()
            .Where(invocation => PcnFacts.ContainsWord(invocation.TargetMethod.Name, "softmax"))
            .ToList();

        if (softmaxCalls.Count > 0)
        {
            foreach (var call in softmaxCalls)
            {
                var argument = call.Arguments.FirstOrDefault()?.Value;

                var hasNegation = PcnFacts.HasNegation(argument) || PcnFacts.HasNegation(operation);
                var hasBeta = PcnFacts.ReferencesName(argument, PcnFacts.IsBetaName) ||
                              PcnFacts.ReferencesName(operation, PcnFacts.IsBetaName);
                var hasFreeEnergy = PcnFacts.ReferencesName(argument, PcnFacts.IsFreeEnergyName) ||
                                    PcnFacts.ReferencesName(operation, PcnFacts.IsFreeEnergyName);

                if (argument is null || !hasNegation || !hasBeta || !hasFreeEnergy)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Rule,
                        call.Syntax.GetLocation(),
                        "the softmax argument is not -β·F"));
                    return;
                }
            }

            return;
        }

        var hardSelection = operation.DescendantsAndSelf()
            .OfType<IInvocationOperation>()
            .FirstOrDefault(invocation => PcnFacts.IsHardSelection(invocation.TargetMethod.Name));

        if (hardSelection is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                hardSelection.Syntax.GetLocation(),
                "a hard selection is used instead of a softmax gate"));
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            method.Identifier.GetLocation(),
            "no softmax gate was found"));
    }
}
