using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Shared "the moved member now lives in another namespace" plumbing for the refactorings that move
/// declarations between types: is that namespace visible from this file, and if not, add the using.
/// </summary>
internal static class RefactoringNamespaces
{
    public static bool IsVisible(SyntaxNode root, string targetNamespace)
    {
        if (root is not CompilationUnitSyntax unit) return true;

        if (unit.Usings.Any(directive => directive.Alias is null &&
                                         string.Equals(directive.Name?.ToString(), targetNamespace, StringComparison.Ordinal)))
            return true;

        var fileNamespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        if (fileNamespace is null) return false;

        return string.Equals(fileNamespace, targetNamespace, StringComparison.Ordinal) ||
               fileNamespace.StartsWith(targetNamespace + ".", StringComparison.Ordinal) ||
               targetNamespace.StartsWith(fileNamespace + ".", StringComparison.Ordinal);
    }

    public static CompilationUnitSyntax AddUsing(CompilationUnitSyntax unit, string targetNamespace)
    {
        var directive = UsingDirective(ParseName(targetNamespace))
            .WithUsingKeyword(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space));

        if (unit.Usings.Count > 0)
        {
            var trailing = unit.Usings.Last().GetTrailingTrivia();
            directive = directive.WithTrailingTrivia(trailing.Count > 0
                ? trailing
                : TriviaList(ElasticCarriageReturnLineFeed));
        }
        else
        {
            directive = directive.WithTrailingTrivia(TriviaList(ElasticCarriageReturnLineFeed, ElasticCarriageReturnLineFeed));
        }

        return unit.WithUsings(unit.Usings.Add(directive));
    }
}
