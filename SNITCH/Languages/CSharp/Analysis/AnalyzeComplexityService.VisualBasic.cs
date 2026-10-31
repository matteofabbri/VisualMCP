using Microsoft.CodeAnalysis;
using Sy = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The Visual Basic half of the Big-O inventory: the method, constructor, operator and accessor blocks
/// are the units the ranking reads, and the block is also the body the walker reads its statements from.
/// </summary>
public sealed partial class AnalyzeComplexityService
{
    // RS1039 claims these calls always return null. They do not: a VB declaration block is exactly what
    // VisualBasicSemanticModel.GetDeclaredSymbol answers for (probed against a real VB compilation), and
    // the symbol is what gives the callable its display name and identity in the catalog.
#pragma warning disable RS1039
    private static IEnumerable<SyntaxNode> VisualBasicCallableDeclarations(SyntaxNode root) =>
        root.DescendantNodes().Where(node => node is
            Sy.MethodBlockSyntax or Sy.ConstructorBlockSyntax or Sy.OperatorBlockSyntax or Sy.AccessorBlockSyntax);

    private static IMethodSymbol? VisualBasicDeclaredSymbol(SemanticModel model, SyntaxNode node) => node switch
    {
        Sy.MethodBlockSyntax method           => model.GetDeclaredSymbol(method) as IMethodSymbol,
        Sy.ConstructorBlockSyntax constructor => model.GetDeclaredSymbol(constructor) as IMethodSymbol,
        Sy.OperatorBlockSyntax @operator      => model.GetDeclaredSymbol(@operator) as IMethodSymbol,
        Sy.AccessorBlockSyntax accessor       => model.GetDeclaredSymbol(accessor) as IMethodSymbol,
        _                                     => null,
    };
#pragma warning restore RS1039
}
