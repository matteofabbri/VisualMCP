using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Refactoring;

[McpServerToolType]
public static class ReorderMembersTool
{
    // Member order within each access group:
    // 1. Constants (const fields)
    // 2. Static readonly fields
    // 3. Static fields
    // 4. Instance readonly fields
    // 5. Instance fields
    // 6. Constructors
    // 7. Finalizer
    // 8. Properties
    // 9. Indexers
    // 10. Events
    // 11. Methods
    // 12. Nested types

    [McpServerTool, Description("Reorder members of all types in a file (or project) by convention: public first, then protected, then private; within each group: constants, fields, constructors, properties, events, methods, nested types. Writes changes to disk. Requires LoadSolution first.")]
    public static async Task<object> ReorderMembers(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Optional: restrict to a single file by absolute path")] string? filePath = null,
        [Description("Dry run â€” report which files would change without writing (default: false)")] bool dryRun = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var modifiedFiles = new List<string>();

        foreach (var project in projects)
        {
            var docs = project.Documents
                .Where(d => d.SourceCodeKind == SourceCodeKind.Regular &&
                            (filePath is null || string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var document in docs)
            {
                var root = await document.GetSyntaxRootAsync();
                if (root is null) continue;

                var rewriter = new MemberReorderRewriter();
                var newRoot = rewriter.Visit(root);

                if (newRoot is null || newRoot.IsEquivalentTo(root)) continue;

                modifiedFiles.Add(document.FilePath ?? "");

                if (!dryRun && document.FilePath is not null)
                    await File.WriteAllTextAsync(document.FilePath, newRoot.ToFullString(), System.Text.Encoding.UTF8);
            }
        }

        return new
        {
            dryRun,
            projectFilter = projectName ?? "all",
            fileFilter = filePath ?? "all",
            filesModified = modifiedFiles.Count,
            files = modifiedFiles,
        };
    }

    private sealed class MemberReorderRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) =>
            ReorderType(base.VisitClassDeclaration(node) as TypeDeclarationSyntax ?? node);

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) =>
            ReorderType(base.VisitStructDeclaration(node) as TypeDeclarationSyntax ?? node);

        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) =>
            ReorderType(base.VisitRecordDeclaration(node) as TypeDeclarationSyntax ?? node);

        private static TypeDeclarationSyntax ReorderType(TypeDeclarationSyntax type)
        {
            var ordered = type.Members
                .OrderBy(AccessOrder)
                .ThenBy(KindOrder)
                .ThenBy(MemberName)
                .ToList();

            // If the order didn't change, skip
            if (ordered.SequenceEqual(type.Members, SyntaxNodeComparer.Instance))
                return type;

            // Preserve leading trivia of the first member on the first reordered member
            var originalFirst = type.Members.FirstOrDefault();
            var reorderedFirst = ordered.FirstOrDefault();
            if (originalFirst is not null && reorderedFirst is not null &&
                !ReferenceEquals(originalFirst, reorderedFirst))
            {
                ordered[0] = ordered[0]
                    .WithLeadingTrivia(originalFirst.GetLeadingTrivia());
            }

            return type.WithMembers(SyntaxFactory.List(ordered));
        }

        private static int AccessOrder(MemberDeclarationSyntax m)
        {
            var mods = m.Modifiers;
            if (mods.Any(SyntaxKind.PublicKeyword))    return 0;
            if (mods.Any(SyntaxKind.ProtectedKeyword)) return 1;
            if (mods.Any(SyntaxKind.InternalKeyword))  return 2;
            return 3; // private or default
        }

        private static int KindOrder(MemberDeclarationSyntax m) => m switch
        {
            FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.ConstKeyword)
                                                                    => 0,
            FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                                           f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                                                                    => 1,
            FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.StaticKeyword)
                                                                    => 2,
            FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                                                                    => 3,
            FieldDeclarationSyntax                                  => 4,
            ConstructorDeclarationSyntax                            => 5,
            DestructorDeclarationSyntax                             => 6,
            PropertyDeclarationSyntax                               => 7,
            IndexerDeclarationSyntax                                => 8,
            EventDeclarationSyntax or EventFieldDeclarationSyntax   => 9,
            MethodDeclarationSyntax                                 => 10,
            TypeDeclarationSyntax or DelegateDeclarationSyntax      => 11,
            _                                                       => 12,
        };

        private static string MemberName(MemberDeclarationSyntax m) => m switch
        {
            MethodDeclarationSyntax meth       => meth.Identifier.Text,
            PropertyDeclarationSyntax prop     => prop.Identifier.Text,
            FieldDeclarationSyntax field       => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "",
            ConstructorDeclarationSyntax ctor  => ctor.Identifier.Text,
            EventDeclarationSyntax evt         => evt.Identifier.Text,
            EventFieldDeclarationSyntax evtf   => evtf.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "",
            TypeDeclarationSyntax nested       => nested.Identifier.Text,
            _                                  => "",
        };
    }

    private sealed class SyntaxNodeComparer : IEqualityComparer<MemberDeclarationSyntax>
    {
        public static readonly SyntaxNodeComparer Instance = new();
        public bool Equals(MemberDeclarationSyntax? x, MemberDeclarationSyntax? y) => ReferenceEquals(x, y);
        public int GetHashCode(MemberDeclarationSyntax obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
