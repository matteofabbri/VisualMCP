using Microsoft.CodeAnalysis;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VbKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;
using CSharpKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The per-language half of clone detection: where a body's statements live, and which tokens are a
/// name or a literal. Everything else — the sliding window, the normalised hash, the clustering — is
/// written once over syntax nodes and runs over either language.
/// </summary>
public sealed partial class FindDuplicateCodeService
{
    /// <summary>
    /// The bodies a VB file can declare with a statement list: a method, a constructor, an operator,
    /// an accessor and a multi-line lambda. A single-line lambda has one statement and never reaches
    /// the minimum window.
    /// </summary>
    private static IEnumerable<MethodBody> VisualBasicMethodBodies(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case VbSyntax.MethodBlockSyntax method:
                    yield return new MethodBody(
                        method.Statements.Cast<SyntaxNode>().ToList(),
                        method.SubOrFunctionStatement.Identifier.Text,
                        LineOf(method));
                    break;

                case VbSyntax.ConstructorBlockSyntax constructor:
                    yield return new MethodBody(
                        constructor.Statements.Cast<SyntaxNode>().ToList(),
                        "New",
                        LineOf(constructor));
                    break;

                case VbSyntax.OperatorBlockSyntax @operator:
                    yield return new MethodBody(
                        @operator.Statements.Cast<SyntaxNode>().ToList(),
                        "Operator " + @operator.OperatorStatement.OperatorToken.Text,
                        LineOf(@operator));
                    break;

                case VbSyntax.AccessorBlockSyntax accessor:
                    yield return new MethodBody(
                        accessor.Statements.Cast<SyntaxNode>().ToList(),
                        accessor.AccessorStatement.AccessorKeyword.Text,
                        LineOf(accessor));
                    break;

                case VbSyntax.MultiLineLambdaExpressionSyntax lambda:
                    yield return new MethodBody(
                        lambda.Statements.Cast<SyntaxNode>().ToList(),
                        "lambda",
                        LineOf(lambda));
                    break;
            }
        }
    }

    private static bool IsIdentifierToken(SyntaxToken token) => token.Language == LanguageNames.VisualBasic
        ? token.RawKind == (int)VbKind.IdentifierToken
        : token.RawKind == (int)CSharpKind.IdentifierToken;

    private static bool IsNumericLiteralToken(SyntaxToken token) => token.Language == LanguageNames.VisualBasic
        ? token.RawKind is (int)VbKind.IntegerLiteralToken or (int)VbKind.FloatingLiteralToken
            or (int)VbKind.DecimalLiteralToken
        : token.RawKind == (int)CSharpKind.NumericLiteralToken;

    private static bool IsStringLiteralToken(SyntaxToken token) => token.Language == LanguageNames.VisualBasic
        ? token.RawKind is (int)VbKind.StringLiteralToken or (int)VbKind.CharacterLiteralToken
            or (int)VbKind.DateLiteralToken
        : token.RawKind is (int)CSharpKind.StringLiteralToken or (int)CSharpKind.InterpolatedStringStartToken;
}
