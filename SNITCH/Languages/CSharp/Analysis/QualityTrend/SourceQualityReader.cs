using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// The metrics of one source text, read syntactically: a historical revision has no compilation, so the
/// numbers must come from the syntax alone. That is the whole point — the same rule set runs on a commit
/// and on the working tree, and the two sides are therefore comparable even though neither was built.
/// <para>
/// Cyclomatic complexity here is the classic count: one path per body, plus one for every <c>if</c>, loop,
/// <c>catch</c>, non-default <c>case</c> label, switch-expression arm, conditional expression, <c>&amp;&amp;</c>,
/// <c>||</c>, <c>??</c> and <c>??=</c>. A local function is its own entry and its decisions are not counted
/// twice; property getters without a body are not methods, and a body-less expression-bodied property is not
/// counted as one.
/// </para>
/// </summary>
public static class SourceQualityReader
{
    /// <summary>A body longer than this is reported as a long method.</summary>
    public const int LongMethodLines = 40;

    private static readonly Regex TodoMarker = new(@"\b(TODO|FIXME|HACK|XXX)\b", RegexOptions.Compiled);

    /// <summary>Read one file. The path is carried through only so the result can name itself.</summary>
    public static FileQuality Read(string path, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source),
            new CSharpParseOptions(LanguageVersion.Preview),
            path);

        var root = tree.GetRoot();
        var text = tree.GetText();

        var lines = text.Lines.Count;
        var sourceLines = text.Lines.Count(line => !string.IsNullOrWhiteSpace(line.ToString()));
        var types = root.DescendantNodes()
            .Count(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

        var methods = ExecutableBodies(root).Select(Measure).ToList();

        return new FileQuality(path, lines, sourceLines, types, CountTodos(root), methods);
    }

    /// <summary>Generated files are not trend material: nobody chose to write them, and they are large.</summary>
    public static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".assemblyinfo.cs", StringComparison.OrdinalIgnoreCase);

    private static MethodQuality Measure(SyntaxNode declaration)
    {
        var body = BodyOf(declaration);
        var span = declaration.GetLocation().GetLineSpan();

        return new MethodQuality(
            NameOf(declaration),
            span.StartLinePosition.Line + 1,
            body is null ? 1 : Complexity(body),
            span.EndLinePosition.Line - span.StartLinePosition.Line + 1);
    }

    private static IEnumerable<SyntaxNode> ExecutableBodies(SyntaxNode root) => root.DescendantNodes()
        .Where(declaration => BodyOf(declaration) is not null);

    private static SyntaxNode? BodyOf(SyntaxNode declaration) => declaration switch
    {
        MethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
        ConstructorDeclarationSyntax constructor => (SyntaxNode?)constructor.Body ?? constructor.ExpressionBody,
        DestructorDeclarationSyntax destructor => (SyntaxNode?)destructor.Body ?? destructor.ExpressionBody,
        OperatorDeclarationSyntax @operator => (SyntaxNode?)@operator.Body ?? @operator.ExpressionBody,
        ConversionOperatorDeclarationSyntax conversion => (SyntaxNode?)conversion.Body ?? conversion.ExpressionBody,
        LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
        AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody,
        _ => null,
    };

    private static string NameOf(SyntaxNode declaration)
    {
        var type = declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

        if (declaration is AccessorDeclarationSyntax accessor)
        {
            var owner = accessor.Ancestors().FirstOrDefault(ancestor =>
                ancestor is PropertyDeclarationSyntax or IndexerDeclarationSyntax);

            var property = owner switch
            {
                PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration.Identifier.ValueText,
                IndexerDeclarationSyntax => "this[]",
                _ => null,
            };

            return string.Join('.', new[] { type, property, accessor.Keyword.ValueText }.Where(part => !string.IsNullOrEmpty(part)));
        }

        var member = declaration switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            DestructorDeclarationSyntax destructor => "~" + destructor.Identifier.ValueText,
            OperatorDeclarationSyntax @operator => "operator " + @operator.OperatorToken.ValueText,
            ConversionOperatorDeclarationSyntax conversion => "operator " + conversion.Type,
            LocalFunctionStatementSyntax local => local.Identifier.ValueText,
            _ => "(body)",
        };

        return string.IsNullOrEmpty(type) ? member : type + "." + member;
    }

    /// <summary>
    /// One base path plus every decision the body itself makes. A local function declared inside the body
    /// is skipped: it is measured as its own method, and counting it here would count its paths twice.
    /// </summary>
    private static int Complexity(SyntaxNode body)
    {
        var complexity = 1;

        foreach (var node in Descendants(body))
            if (IsDecision(node)) complexity++;

        return complexity;
    }

    private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
    {
        foreach (var child in node.ChildNodes())
        {
            if (child is LocalFunctionStatementSyntax) continue;

            yield return child;

            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static bool IsDecision(SyntaxNode node) => node switch
    {
        IfStatementSyntax => true,
        ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax
            or WhileStatementSyntax or DoStatementSyntax => true,
        CatchClauseSyntax => true,
        ConditionalExpressionSyntax => true,
        SwitchExpressionArmSyntax => true,
        CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax => true,
        BinaryExpressionSyntax binary =>
            binary.IsKind(SyntaxKind.LogicalAndExpression) ||
            binary.IsKind(SyntaxKind.LogicalOrExpression) ||
            binary.IsKind(SyntaxKind.CoalesceExpression),
        AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression),
        _ => false,
    };

    private static int CountTodos(SyntaxNode root) => root.DescendantTrivia(descendIntoTrivia: true)
        .Count(trivia => IsComment(trivia) && TodoMarker.IsMatch(trivia.ToString()));

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
}
