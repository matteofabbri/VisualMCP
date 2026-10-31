using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The Visual Basic half of the cohesion metric: VB spells a type as a block and a name as its own
/// identifier node, but the metric underneath — which members use which field, and how those uses
/// cluster — is the same union-find over the same symbols.
/// </summary>
public sealed partial class GetCohesionMetricsService
{
    /// <summary>A class, a structure or a module; interfaces, enums and delegates declare no fields.</summary>
    private static bool IsVisualBasicTypeDeclaration(SyntaxNode node) =>
        node is ClassBlockSyntax or StructureBlockSyntax or ModuleBlockSyntax;

    /// <summary>
    /// The names a member reads. VB declares a method on its statement and puts the body in the block
    /// that follows, so the statement alone would hide every field the method touches: the enclosing
    /// block is the scope that has to be scanned.
    /// </summary>
    private static IEnumerable<SyntaxNode> VisualBasicNameNodes(SyntaxNode node)
    {
        var scope = node.Parent is MethodBlockSyntax or ConstructorBlockSyntax or OperatorBlockSyntax
                or AccessorBlockSyntax or PropertyBlockSyntax or EventBlockSyntax
            ? node.Parent
            : node;

        return scope!.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>();
    }
}
