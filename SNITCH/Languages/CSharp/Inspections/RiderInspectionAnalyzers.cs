using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Snitch.Languages.CSharp.Inspections.Rider;

/// <summary>
/// The ReSharper-shaped inspections this engine implements natively, under ReSharper's own ids. They are a
/// curated set — the parts of the proprietary catalogue that add a detection Roslyn's packs and the style
/// engine do not already make — not a port of the ~2500 closed-source inspections.
/// </summary>
/// <remarks>
/// A Roslyn diagnostic id must be a C# identifier, so ReSharper's dotted ids are spelled with an
/// underscore: <c>ConvertToConstant.Local</c> is reported as <c>ConvertToConstant_Local</c> and
/// <c>MemberCanBeMadeStatic.Global</c> as <c>MemberCanBeMadeStatic_Global</c>. The map tool names both.
/// </remarks>
internal static class RiderRules
{
    public static DiagnosticDescriptor Rule(string id, string title, string message, DiagnosticSeverity severity) =>
        new(id, title, message, "Rider", severity, isEnabledByDefault: true);
}

/// <summary>ConvertToConstant.Local: a local assigned once with a constant value can be const.</summary>
public sealed class ConvertToConstantLocalAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "ConvertToConstant_Local", "Convert to constant",
        "The local '{0}' is assigned once with a constant value and can be declared const",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LocalDeclarationStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var declaration = (LocalDeclarationStatementSyntax)context.Node;
        if (declaration.IsConst) return;

        var model = context.SemanticModel;
        foreach (var variable in declaration.Declaration.Variables)
        {
            if (variable.Initializer is null) continue;

            var constant = model.GetConstantValue(variable.Initializer.Value, context.CancellationToken);
            if (!constant.HasValue) continue;

            if (model.GetDeclaredSymbol(variable, context.CancellationToken) is not ILocalSymbol local) continue;
            var type = local.Type;
            if (type.SpecialType == SpecialType.None && type.TypeKind != TypeKind.Enum) continue;

            if (IsWrittenLater(declaration, local, model, context.CancellationToken)) continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, variable.Identifier.GetLocation(), local.Name));
        }
    }

    /// <summary>Any assignment, increment or out/ref use of the local after its declaration.</summary>
    private static bool IsWrittenLater(
        LocalDeclarationStatementSyntax declaration, ILocalSymbol local, SemanticModel model, CancellationToken cancellationToken)
    {
        var scope = declaration.Parent;
        if (scope is null) return true;

        foreach (var node in scope.DescendantNodes())
        {
            if (node is not IdentifierNameSyntax identifier) continue;
            if (node.SpanStart < declaration.Span.End) continue;
            if (!identifier.Identifier.ValueText.Equals(local.Name, StringComparison.Ordinal)) continue;
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, local)) continue;

            for (var current = identifier.Parent; current is not null; current = current.Parent)
            {
                switch (current)
                {
                    case AssignmentExpressionSyntax assignment when assignment.Left.Span.Contains(identifier.Span):
                        return true;
                    case PrefixUnaryExpressionSyntax prefix
                        when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                        return true;
                    case PostfixUnaryExpressionSyntax postfix
                        when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                        return true;
                    case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ||
                                                      argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword):
                        return true;
                    case StatementSyntax:
                        goto done;
                }
            }

            done: ;
        }

        return false;
    }
}

/// <summary>LoopVariableIsNeverChangedInsideLoop: the for index written inside the body is a bug smell.</summary>
public sealed class LoopVariableChangedAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "LoopVariableIsNeverChangedInsideLoop", "Loop variable is changed inside the loop",
        "The loop variable '{0}' is modified inside the loop body",
        DiagnosticSeverity.Warning);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ForStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var loop = (ForStatementSyntax)context.Node;
        string? name = null;

        switch (loop.Declaration?.Variables)
        {
            case { Count: 1 } variables:
                name = variables[0].Identifier.ValueText;
                break;
        }

        if (name is null && loop.Initializers.Count == 1 &&
            loop.Initializers[0] is AssignmentExpressionSyntax assignment)
            name = assignment.Left.ToString();

        if (name is null) return;

        foreach (var node in loop.Statement.DescendantNodes())
        {
            if (node is not IdentifierNameSyntax identifier) continue;
            if (!identifier.Identifier.ValueText.Equals(name, StringComparison.Ordinal)) continue;

            for (var current = identifier.Parent; current is not null; current = current.Parent)
            {
                switch (current)
                {
                    case AssignmentExpressionSyntax write when write.Left.Span.Contains(identifier.Span):
                        context.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation(), name));
                        return;
                    case PrefixUnaryExpressionSyntax prefix
                        when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    case PostfixUnaryExpressionSyntax postfix
                        when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                        context.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation(), name));
                        return;
                    case StatementSyntax:
                        goto next;
                }
            }

            next: ;
        }
    }
}

/// <summary>MemberCanBeMadeStatic.Global: a method that touches no instance state can be static.</summary>
public sealed class MemberCanBeMadeStaticAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "MemberCanBeMadeStatic_Global", "Member can be made static",
        "The method '{0}' does not use instance state and can be static",
        DiagnosticSeverity.Info);

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
        if (method.Body is null) return;
        if (method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword) ||
                                             modifier.IsKind(SyntaxKind.VirtualKeyword) ||
                                             modifier.IsKind(SyntaxKind.AbstractKeyword) ||
                                             modifier.IsKind(SyntaxKind.OverrideKeyword) ||
                                             modifier.IsKind(SyntaxKind.PartialKeyword) ||
                                             modifier.IsKind(SyntaxKind.ExternKeyword)))
            return;

        if (context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken) is not IMethodSymbol symbol) return;
        if (symbol.ExplicitInterfaceImplementations.Length > 0 || symbol.IsOverride) return;
        if (symbol.ContainingType is null) return;

        var containing = symbol.ContainingType;
        foreach (var node in method.Body.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ThisExpressionSyntax:
                case BaseExpressionSyntax:
                    return;
            }

            if (node is not IdentifierNameSyntax && node is not GenericNameSyntax)
                continue;

            var used = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;
            if (used is null) continue;
            if (used.IsStatic) continue;

            var owner = used switch
            {
                IFieldSymbol field => field.ContainingType,
                IPropertySymbol property => property.ContainingType,
                IMethodSymbol called => called.ContainingType,
                IEventSymbol @event => @event.ContainingType,
                _ => null,
            };

            if (owner is null) continue;
            if (SymbolEqualityComparer.Default.Equals(owner, containing) ||
                SymbolEqualityComparer.Default.Equals(owner, containing.BaseType))
                return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), method.Identifier.ValueText));
    }
}

/// <summary>RedundantArgument: an argument that repeats the parameter's default value.</summary>
public sealed class RedundantArgumentAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "RedundantArgument", "Redundant argument",
        "The argument '{0}' equals the parameter's default value and can be omitted",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;

        var arguments = invocation.ArgumentList.Arguments;
        for (var index = 0; index < arguments.Count && index < method.Parameters.Length; index++)
        {
            var parameter = method.Parameters[index];
            if (!parameter.IsOptional || parameter.ExplicitDefaultValue is null) continue;

            var argument = arguments[index];
            if (argument.NameColon is not null) continue;
            var constant = context.SemanticModel.GetConstantValue(argument.Expression, context.CancellationToken);
            if (!constant.HasValue) continue;
            if (!SameValue(constant.Value, parameter.ExplicitDefaultValue)) continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, argument.GetLocation(), parameter.Name));
        }
    }

    private static bool SameValue(object? left, object? right)
    {
        if (Equals(left, right)) return true;
        if (left is null || right is null) return false;

        try { return Convert.ToDecimal(left) == Convert.ToDecimal(right); }
        catch (Exception) { return false; }
    }
}

/// <summary>EmptyConstructor: a constructor with an empty body and no initializer arguments.</summary>
public sealed class EmptyConstructorAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "EmptyConstructor", "Empty constructor",
        "The constructor can be removed: it has an empty body and no initializer arguments",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ConstructorDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var constructor = (ConstructorDeclarationSyntax)context.Node;
        if (constructor.Body is null || constructor.Body.Statements.Count > 0) return;
        if (constructor.Initializer is { ArgumentList.Arguments.Count: > 0 }) return;
        if (constructor.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword))) return;
        if (constructor.ParameterList.Parameters.Count > 0) return;

        // Removing it is only safe when this is the type's single constructor and it is public: the
        // implicit parameterless one then takes its place with the same shape.
        if (!constructor.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword))) return;
        if (context.SemanticModel.GetDeclaredSymbol(constructor, context.CancellationToken) is not IMethodSymbol symbol) return;
        if (symbol.ContainingType.InstanceConstructors.Length != 1) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, constructor.Identifier.GetLocation()));
    }
}

/// <summary>RedundantIfElseBlock: if (c) return true; else return false; — the condition is the answer.</summary>
public sealed class RedundantIfElseAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "RedundantIfElseBlock", "Redundant if/else block",
        "The if/else only returns opposite boolean literals: return the condition (or its negation) instead",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.IfStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var statement = (IfStatementSyntax)context.Node;
        if (statement.Else is null) return;

        if (!TryBooleanReturn(statement.Statement, out var whenTrue)) return;
        if (!TryBooleanReturn(statement.Else.Statement, out var whenFalse)) return;
        if (whenTrue == whenFalse) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.IfKeyword.GetLocation()));
    }

    private static bool TryBooleanReturn(StatementSyntax statement, out bool value)
    {
        value = false;
        var returned = statement switch
        {
            ReturnStatementSyntax single => single.Expression,
            BlockSyntax { Statements: [ReturnStatementSyntax only] } => only.Expression,
            _ => null,
        };

        if (returned is not LiteralExpressionSyntax literal) return false;
        if (literal.IsKind(SyntaxKind.TrueLiteralExpression)) { value = true; return true; }
        if (literal.IsKind(SyntaxKind.FalseLiteralExpression)) { value = false; return true; }
        return false;
    }
}

/// <summary>ConditionIsAlwaysTrueOrFalse: a computed constant condition (not the literal true/false).</summary>
public sealed class ConstantConditionAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "ConditionIsAlwaysTrueOrFalse", "Condition is always true or false",
        "The condition is always {0}",
        DiagnosticSeverity.Warning);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIf, SyntaxKind.IfStatement);
        context.RegisterSyntaxNodeAction(AnalyzeWhile, SyntaxKind.WhileStatement);
        context.RegisterSyntaxNodeAction(AnalyzeFor, SyntaxKind.ForStatement);
    }

    private static void AnalyzeIf(SyntaxNodeAnalysisContext context) =>
        Report(context, ((IfStatementSyntax)context.Node).Condition);

    private static void AnalyzeWhile(SyntaxNodeAnalysisContext context) =>
        Report(context, ((WhileStatementSyntax)context.Node).Condition);

    private static void AnalyzeFor(SyntaxNodeAnalysisContext context) =>
        Report(context, ((ForStatementSyntax)context.Node).Condition);

    private static void Report(SyntaxNodeAnalysisContext context, ExpressionSyntax? condition)
    {
        if (condition is null) return;
        // The literal forms are the writer's deliberate choice (while (true)); only computed constants are suspicious.
        if (condition is LiteralExpressionSyntax) return;

        var constant = context.SemanticModel.GetConstantValue(condition, context.CancellationToken);
        if (!constant.HasValue || constant.Value is not bool value) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, condition.GetLocation(), value ? "true" : "false"));
    }
}

/// <summary>RedundantBaseConstructorCall: an explicit parameterless base() initializer.</summary>
public sealed class RedundantBaseConstructorCallAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "RedundantBaseConstructorCall", "Redundant base constructor call",
        "The explicit base() call adds nothing: the base constructor runs anyway",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ConstructorDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var constructor = (ConstructorDeclarationSyntax)context.Node;
        if (constructor.Initializer is not { } initializer) return;
        if (!initializer.IsKind(SyntaxKind.BaseConstructorInitializer)) return;
        if (initializer.ArgumentList.Arguments.Count > 0) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, initializer.GetLocation()));
    }
}
