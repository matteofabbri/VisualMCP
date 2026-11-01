using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Snitch.Languages.CSharp.Inspections.Rider;

/// <summary>
/// The ReSharper-shaped inspections this engine implements natively, under ReSharper's own ids. They are a
/// curated set — the parts of the proprietary catalogue that add a detection Roslyn's packs and the style
/// engine do not already make — not a port of the closed-source catalogue.
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

/// <summary>ConvertToCompoundAssignment: x = x + y can be written x += y.</summary>
public sealed class ConvertToCompoundAssignmentAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "ConvertToCompoundAssignment", "Convert to compound assignment",
        "The assignment can be written as a compound assignment",
        DiagnosticSeverity.Info);

    /// <summary>The binary operator and the compound assignment it maps to.</summary>
    internal static readonly Dictionary<SyntaxKind, SyntaxKind> CompoundOperators = new()
    {
        [SyntaxKind.AddExpression] = SyntaxKind.AddAssignmentExpression,
        [SyntaxKind.SubtractExpression] = SyntaxKind.SubtractAssignmentExpression,
        [SyntaxKind.MultiplyExpression] = SyntaxKind.MultiplyAssignmentExpression,
        [SyntaxKind.DivideExpression] = SyntaxKind.DivideAssignmentExpression,
        [SyntaxKind.ModuloExpression] = SyntaxKind.ModuloAssignmentExpression,
        [SyntaxKind.BitwiseAndExpression] = SyntaxKind.AndAssignmentExpression,
        [SyntaxKind.BitwiseOrExpression] = SyntaxKind.OrAssignmentExpression,
        [SyntaxKind.ExclusiveOrExpression] = SyntaxKind.ExclusiveOrAssignmentExpression,
        [SyntaxKind.LeftShiftExpression] = SyntaxKind.LeftShiftAssignmentExpression,
        [SyntaxKind.RightShiftExpression] = SyntaxKind.RightShiftAssignmentExpression,
    };

    /// <summary>The token that spells each compound assignment (<c>+=</c>, …), which the fix writes.</summary>
    internal static readonly Dictionary<SyntaxKind, SyntaxKind> CompoundTokens = new()
    {
        [SyntaxKind.AddExpression] = SyntaxKind.PlusEqualsToken,
        [SyntaxKind.SubtractExpression] = SyntaxKind.MinusEqualsToken,
        [SyntaxKind.MultiplyExpression] = SyntaxKind.AsteriskEqualsToken,
        [SyntaxKind.DivideExpression] = SyntaxKind.SlashEqualsToken,
        [SyntaxKind.ModuloExpression] = SyntaxKind.PercentEqualsToken,
        [SyntaxKind.BitwiseAndExpression] = SyntaxKind.AmpersandEqualsToken,
        [SyntaxKind.BitwiseOrExpression] = SyntaxKind.BarEqualsToken,
        [SyntaxKind.ExclusiveOrExpression] = SyntaxKind.CaretEqualsToken,
        [SyntaxKind.LeftShiftExpression] = SyntaxKind.LessThanLessThanEqualsToken,
        [SyntaxKind.RightShiftExpression] = SyntaxKind.GreaterThanGreaterThanEqualsToken,
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (!IsSimpleTarget(assignment.Left)) return;
        if (assignment.Right is not BinaryExpressionSyntax binary) return;
        if (!CompoundOperators.ContainsKey(binary.Kind())) return;
        if (!SyntaxFactory.AreEquivalent(assignment.Left, binary.Left)) return;

        var type = context.SemanticModel.GetTypeInfo(assignment.Left, context.CancellationToken).Type;
        if (type is null || !IsCompoundable(type)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation()));
    }

    /// <summary>A target whose evaluation has no side effect: a name, or a member access over one.</summary>
    internal static bool IsSimpleTarget(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax member => IsSimpleTarget(member.Expression),
        ThisExpressionSyntax or BaseExpressionSyntax => true,
        _ => false,
    };

    /// <summary>A type the built-in compound operators are defined for.</summary>
    internal static bool IsCompoundable(ITypeSymbol type) => type.TypeKind == TypeKind.Enum || type.SpecialType is
        SpecialType.System_String or SpecialType.System_Char or SpecialType.System_Boolean or
        SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or
        SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or
        SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or
        SpecialType.System_Byte or SpecialType.System_SByte;
}

/// <summary>RedundantLambdaSignatureParentheses: (x) =&gt; … needs no parentheses around one untyped parameter.</summary>
public sealed class RedundantLambdaSignatureParenthesesAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "RedundantLambdaSignatureParentheses", "Redundant lambda signature parentheses",
        "The parentheses around the single lambda parameter are redundant",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ParenthesizedLambdaExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var lambda = (ParenthesizedLambdaExpressionSyntax)context.Node;
        if (lambda.ParameterList.Parameters.Count != 1) return;

        var parameter = lambda.ParameterList.Parameters[0];
        if (parameter.Type is not null) return;
        if (parameter.Modifiers.Count > 0) return;
        if (parameter.Default is not null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, lambda.ParameterList.GetLocation()));
    }
}

/// <summary>
/// UseNameofExpression: a string literal that repeats the name of a parameter, in the places where the
/// name is a reference (the ArgumentException family, and any argument bound to a paramName parameter).
/// </summary>
public sealed class UseNameofExpressionAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "UseNameofExpression", "Use nameof expression",
        "The string literal '{0}' can be replaced with nameof({0})",
        DiagnosticSeverity.Info);

    private static readonly HashSet<string> ArgumentExceptionTypes = new(StringComparer.Ordinal)
    {
        "ArgumentNullException", "ArgumentException", "ArgumentOutOfRangeException",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is null) return;
        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol constructor)
            return;
        if (!ArgumentExceptionTypes.Contains(constructor.ContainingType.Name)) return;

        foreach (var argument in creation.ArgumentList.Arguments)
        {
            if (IsParameterNameLiteral(context, argument.Expression))
                context.ReportDiagnostic(Diagnostic.Create(Rule, argument.Expression.GetLocation(),
                    ((LiteralExpressionSyntax)argument.Expression).Token.ValueText));
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;

        for (var index = 0; index < invocation.ArgumentList.Arguments.Count && index < method.Parameters.Length; index++)
        {
            var parameter = method.Parameters[index];
            if (!parameter.Name.Contains("paramName", StringComparison.OrdinalIgnoreCase) &&
                !parameter.Name.Contains("parameterName", StringComparison.OrdinalIgnoreCase))
                continue;

            var expression = invocation.ArgumentList.Arguments[index].Expression;
            if (IsParameterNameLiteral(context, expression))
                context.ReportDiagnostic(Diagnostic.Create(Rule, expression.GetLocation(),
                    ((LiteralExpressionSyntax)expression).Token.ValueText));
        }
    }

    /// <summary>The expression is a string literal that names a parameter of the enclosing member.</summary>
    private static bool IsParameterNameLiteral(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        if (expression is not LiteralExpressionSyntax literal) return false;
        if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) return false;

        var name = literal.Token.ValueText;
        if (name.Length == 0) return false;

        for (var current = expression.Parent; current is not null; current = current.Parent)
        {
            if (current is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax or
                AccessorDeclarationSyntax or OperatorDeclarationSyntax or IndexerDeclarationSyntax))
                continue;

            var declared = context.SemanticModel.GetDeclaredSymbol(current, context.CancellationToken);
            if (declared is null) return false;

            var parameters = declared switch
            {
                IMethodSymbol method => method.Parameters,
                IPropertySymbol property => property.Parameters,
                _ => default,
            };

            foreach (var parameter in parameters)
                if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
                    return true;

            return false;
        }

        return false;
    }
}

/// <summary>StringLiteralAsInterpolationArgument: a literal inside an interpolation is plain text.</summary>
public sealed class StringLiteralAsInterpolationArgumentAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "StringLiteralAsInterpolationArgument", "Merge string literal with containing interpolation",
        "The string literal can be merged into the interpolated string as text",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.Interpolation);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var interpolation = (InterpolationSyntax)context.Node;
        if (interpolation.AlignmentClause is not null || interpolation.FormatClause is not null) return;
        if (interpolation.Expression is not LiteralExpressionSyntax literal) return;
        if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) return;
        if (!literal.Token.Text.StartsWith("\"", StringComparison.Ordinal)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, interpolation.GetLocation()));
    }
}

/// <summary>RedundantCheckBeforeAssignment: if (a != b) a = b; assigns in both cases.</summary>
public sealed class RedundantCheckBeforeAssignmentAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "RedundantCheckBeforeAssignment", "Redundant check before assignment",
        "The guard repeats the assignment: the value is written either way",
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
        if (statement.Else is not null) return;
        if (statement.Condition is not BinaryExpressionSyntax condition) return;
        if (!condition.IsKind(SyntaxKind.NotEqualsExpression)) return;

        var body = statement.Statement switch
        {
            ExpressionStatementSyntax single => single.Expression,
            BlockSyntax { Statements: [ExpressionStatementSyntax only] } => only.Expression,
            _ => null,
        };
        if (body is not AssignmentExpressionSyntax assignment) return;
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return;

        if (!SyntaxFactory.AreEquivalent(assignment.Left, condition.Left)) return;
        if (!SyntaxFactory.AreEquivalent(assignment.Right, condition.Right)) return;

        // The rewrite drops one evaluation of each side, so both must be side-effect free.
        if (!IsSideEffectFree(context, assignment.Left)) return;
        if (!IsSideEffectFree(context, assignment.Right)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.IfKeyword.GetLocation()));
    }

    private static bool IsSideEffectFree(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax:
                return true;
            case IdentifierNameSyntax:
                var symbol = context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol;
                return symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol;
            case MemberAccessExpressionSyntax member:
                return IsSideEffectFree(context, member.Expression);
            default:
                return false;
        }
    }
}

/// <summary>
/// RedundantStringInterpolation: an interpolated string with no interpolation is a plain string.
/// Ported from <c>RedundantInterpolatedStringExpressionAnalyzer</c>: a trivial interpolated string of the
/// non-verbatim kind, not a member of an interpolated-string concatenation.
/// </summary>
public sealed class RedundantStringInterpolationAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "RedundantStringInterpolation", "Redundant string interpolation",
        "The interpolated string has no interpolation and can be a plain string",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InterpolatedStringExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var expression = (InterpolatedStringExpressionSyntax)context.Node;

        // "$" is the interpolated kind; "$@" and "@$" are the verbatim kinds ReSharper leaves alone.
        if (!expression.StringStartToken.IsKind(SyntaxKind.InterpolatedStringStartToken)) return;
        if (expression.Contents.Any(content => content is InterpolationSyntax)) return;
        if (IsPartOfInterpolatedConcatenation(expression)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, expression.StringStartToken.GetLocation()));
    }

    /// <summary>Two adjacent interpolated strings are merged by the compiler; the trivial one stays as written.</summary>
    private static bool IsPartOfInterpolatedConcatenation(InterpolatedStringExpressionSyntax expression) =>
        expression.Parent is BinaryExpressionSyntax binary &&
        binary.IsKind(SyntaxKind.AddExpression) &&
        (binary.Left is InterpolatedStringExpressionSyntax || binary.Right is InterpolatedStringExpressionSyntax);
}

/// <summary>
/// MethodOverloadWithOptionalParameter: an overload that only adds optional parameters is shadowed by the
/// shorter one, because every call it accepts the other accepts too.
/// Ported from <c>MethodOverloadWithOptionalParametersProblemAnalyzer</c>.
/// </summary>
public sealed class MethodOverloadWithOptionalParameterAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "MethodOverloadWithOptionalParameter", "Method overload with optional parameter",
        "The parameter '{0}' is hidden by an overload of '{1}' with no such parameter",
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
        var declaration = (MethodDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not IMethodSymbol method) return;
        if (method.IsOverride || method.ExplicitInterfaceImplementations.Length > 0) return;
        if (method.ContainingType is null) return;

        var parameters = method.Parameters;
        if (parameters.Length == 0) return;

        var overloads = method.ContainingType.GetMembers(method.Name)
            .OfType<IMethodSymbol>()
            .Where(other => !SymbolEqualityComparer.Default.Equals(other, method))
            .Where(other => !other.IsOverride && other.ExplicitInterfaceImplementations.Length == 0)
            .Where(other => other.Arity == method.Arity)
            .ToList();
        if (overloads.Count == 0) return;

        // Optional parameters are trailing, so the candidates are the prefixes of the signature.
        for (var index = parameters.Length - 1; index >= 0 && parameters[index].IsOptional; index--)
        {
            if (!overloads.Any(overload => IsPrefix(overload, parameters, index))) continue;

            var parameter = declaration.ParameterList.Parameters[index];
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, parameter.Identifier.GetLocation(), parameters[index].Name, method.Name));
        }
    }

    private static bool IsPrefix(IMethodSymbol overload, ImmutableArray<IParameterSymbol> parameters, int count)
    {
        if (overload.Parameters.Length != count) return false;

        for (var index = 0; index < count; index++)
        {
            if (overload.Parameters[index].RefKind != parameters[index].RefKind) return false;
            if (!SymbolEqualityComparer.Default.Equals(overload.Parameters[index].Type, parameters[index].Type)) return false;
        }

        return true;
    }
}

/// <summary>
/// ReplaceSequenceEqualWithConstantPattern: a char span compared to a string literal with SequenceEqual is
/// the C# 11 constant pattern. Ported from <c>ReplaceSequenceEqualWithConstantPatternProblemAnalyzer</c>.
/// </summary>
public sealed class ReplaceSequenceEqualWithConstantPatternAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "ReplaceSequenceEqualWithConstantPattern", "Replace SequenceEqual with a constant pattern",
        "The span can be compared to the literal with a constant pattern: x is \"…\"",
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
        if (invocation.ArgumentList.Arguments.Count != 1) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;
        if (!member.Name.Identifier.ValueText.Equals("SequenceEqual", StringComparison.Ordinal)) return;

        // A constant pattern over a string needs C# 11.
        if (invocation.SyntaxTree.Options is not CSharpParseOptions parseOptions ||
            parseOptions.LanguageVersion < LanguageVersion.CSharp11)
            return;

        var argument = invocation.ArgumentList.Arguments[0].Expression;
        if (argument is not LiteralExpressionSyntax literal) return;
        if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) return;
        if (!literal.Token.Text.StartsWith("\"", StringComparison.Ordinal)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method) return;

        // An extension method invoked with a member access resolves to its reduced form, which drops the
        // receiver from Parameters; the declared symbol is what carries the two parameters.
        var declared = method.ReducedFrom ?? method;
        if (declared.Name != "SequenceEqual" || declared.Parameters.Length != 2 || !declared.IsExtensionMethod) return;
        if (!IsMemoryExtensions(declared.ContainingType)) return;

        if (context.SemanticModel.GetTypeInfo(member.Expression, context.CancellationToken).Type is not INamedTypeSymbol qualifier)
            return;
        if (!IsCharSpan(qualifier)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsMemoryExtensions(INamedTypeSymbol? type) =>
        type is { Name: "MemoryExtensions" } && type.ContainingNamespace?.ToDisplayString() == "System";

    private static bool IsCharSpan(INamedTypeSymbol type) =>
        type is { Name: "Span" or "ReadOnlySpan", TypeArguments.Length: 1 } &&
        type.ContainingNamespace?.ToDisplayString() == "System" &&
        type.TypeArguments[0].SpecialType == SpecialType.System_Char;
}

/// <summary>
/// RedundantPatternParentheses: parentheses that pattern precedence does not require.
/// Ported from <c>RedundantPatternParenthesesProblemAnalyzer</c>.
/// </summary>
public sealed class RedundantPatternParenthesesAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "RedundantPatternParentheses", "Redundant pattern parentheses",
        "The parentheses around the pattern are redundant",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ParenthesizedPattern);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var parenthesized = (ParenthesizedPatternSyntax)context.Node;
        if (parenthesized.Pattern is not { } pattern) return;
        if (!ParenthesesAreRedundant(parenthesized, pattern)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, parenthesized.GetLocation()));
    }

    /// <summary>In patterns <c>not</c> binds tighter than <c>and</c>, which binds tighter than <c>or</c>.</summary>
    private static bool ParenthesesAreRedundant(ParenthesizedPatternSyntax parenthesized, PatternSyntax inner) =>
        parenthesized.Parent switch
        {
            UnaryPatternSyntax => false,
            BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.AndPattern) => !inner.IsKind(SyntaxKind.OrPattern),
            _ => true,
        };
}

/// <summary>
/// MergeIntoPattern: <c>x is A &amp;&amp; x is B</c> is one pattern, <c>x is A and B</c>.
/// Ported from <c>SequentialPatternCheck</c> / <c>MergeIntoPatternWarning</c>: only the conjunction of two
/// checks on the same operand is merged, and an or-pattern is left alone because <c>and</c> binds tighter.
/// </summary>
public sealed class MergeIntoPatternAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "MergeIntoPattern", "Merge into pattern",
        "The two checks on the same value can be one pattern: 'is A and B'",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LogicalAndExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (!TryMatch(binary, out _, out _)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.OperatorToken.GetLocation()));
    }

    /// <summary>The two pattern checks this conjunction merges, when it is the mergeable shape.</summary>
    internal static bool TryMatch(
        BinaryExpressionSyntax binary, out IsPatternExpressionSyntax left, out IsPatternExpressionSyntax right)
    {
        left = null!;
        right = null!;

        if (binary.Left is not IsPatternExpressionSyntax leftCheck) return false;
        if (binary.Right is not IsPatternExpressionSyntax rightCheck) return false;
        if (!SyntaxFactory.AreEquivalent(leftCheck.Expression, rightCheck.Expression)) return false;

        // (a or b) and c is not a or b and c: an or-pattern on either side keeps its own grouping.
        if (leftCheck.Pattern.IsKind(SyntaxKind.OrPattern) || rightCheck.Pattern.IsKind(SyntaxKind.OrPattern)) return false;

        left = leftCheck;
        right = rightCheck;
        return true;
    }
}

/// <summary>
/// ReplaceWithStringIsNullOrEmpty: <c>s == null || s.Length == 0</c> is <c>string.IsNullOrEmpty(s)</c>.
/// Ported from <c>ReplaceWithStringIsNullOrEmptyWarning</c>: the positive form inside <c>||</c> and the
/// negated one inside <c>&amp;&amp;</c>, on one side-effect-free string operand.
/// </summary>
public sealed class ReplaceWithStringIsNullOrEmptyAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = RiderRules.Rule(
        "ReplaceWithStringIsNullOrEmpty", "Use string.IsNullOrEmpty",
        "The null-or-empty check on '{0}' can be written with string.IsNullOrEmpty",
        DiagnosticSeverity.Info);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeOr, SyntaxKind.LogicalOrExpression);
        context.RegisterSyntaxNodeAction(AnalyzeAnd, SyntaxKind.LogicalAndExpression);
    }

    private static void AnalyzeOr(SyntaxNodeAnalysisContext context) => Analyze(context, negated: false);

    private static void AnalyzeAnd(SyntaxNodeAnalysisContext context) => Analyze(context, negated: true);

    private static void Analyze(SyntaxNodeAnalysisContext context, bool negated)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (!TryMatch(binary, context.SemanticModel, negated, out var operand)) return;

        if (context.SemanticModel.GetTypeInfo(operand, context.CancellationToken).Type?.SpecialType !=
            SpecialType.System_String)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), operand.ToString()));
    }

    /// <summary>The string operand when the pair is a null check and an empty check in the same direction.</summary>
    internal static bool TryMatch(
        BinaryExpressionSyntax binary, SemanticModel? model, bool negated, out ExpressionSyntax operand)
    {
        operand = null!;

        if (!TryNullCheck(binary.Left, out var nullChecked, out var isNullCheck)) return false;
        if (isNullCheck == negated) return false;
        if (!TryEmptyCheck(binary.Right, out var emptyChecked, out var isEmptyCheck, model)) return false;
        if (isEmptyCheck == negated) return false;
        if (!SyntaxFactory.AreEquivalent(nullChecked, emptyChecked)) return false;
        if (!IsSideEffectFree(nullChecked)) return false;

        operand = nullChecked;
        return true;
    }

    private static bool TryNullCheck(ExpressionSyntax expression, out ExpressionSyntax operand, out bool isNull)
    {
        operand = null!;
        isNull = false;
        if (expression is not BinaryExpressionSyntax binary) return false;
        if (!binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression)) return false;

        isNull = binary.IsKind(SyntaxKind.EqualsExpression);
        if (IsNullLiteral(binary.Right)) { operand = binary.Left; return true; }
        if (IsNullLiteral(binary.Left)) { operand = binary.Right; return true; }
        return false;
    }

    private static bool TryEmptyCheck(
        ExpressionSyntax expression, out ExpressionSyntax operand, out bool isEmpty, SemanticModel? model)
    {
        operand = null!;
        isEmpty = false;

        if (expression is not BinaryExpressionSyntax binary) return false;

        // s.Length == 0 / s.Length < 1 (empty) and s.Length != 0 / s.Length > 0 (not empty).
        if (binary.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Length" } length)
        {
            if (binary.Right is not LiteralExpressionSyntax literal || literal.Token.Value is not int value) return false;

            operand = length.Expression;
            if (binary.IsKind(SyntaxKind.EqualsExpression) && value == 0) { isEmpty = true; return true; }
            if (binary.IsKind(SyntaxKind.LessThanExpression) && value == 1) { isEmpty = true; return true; }
            if (binary.IsKind(SyntaxKind.LessThanOrEqualExpression) && value == 0) { isEmpty = true; return true; }
            if (binary.IsKind(SyntaxKind.NotEqualsExpression) && value == 0) { isEmpty = false; return true; }
            if (binary.IsKind(SyntaxKind.GreaterThanExpression) && value == 0) { isEmpty = false; return true; }
            return false;
        }

        // s == "" / s == string.Empty (and the negations).
        if (!binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression)) return false;
        isEmpty = binary.IsKind(SyntaxKind.EqualsExpression);

        if (IsEmptyString(binary.Right, model)) { operand = binary.Left; return true; }
        if (IsEmptyString(binary.Left, model)) { operand = binary.Right; return true; }
        return false;
    }

    private static bool IsNullLiteral(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.NullLiteralExpression);

    private static bool IsEmptyString(ExpressionSyntax expression, SemanticModel? model) =>
        expression is LiteralExpressionSyntax { Token.Value: string value } && value.Length == 0 ||
        model?.GetSymbolInfo(expression).Symbol is IFieldSymbol { Name: "Empty" } field &&
        field.ContainingType.SpecialType == SpecialType.System_String;

    private static bool IsSideEffectFree(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax => true,
        ThisExpressionSyntax => true,
        MemberAccessExpressionSyntax member => IsSideEffectFree(member.Expression),
        _ => false,
    };
}
