using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Implement INotifyPropertyChanged" on a Visual Basic class: the Implements clause, the event,
/// a caller-name helper, and one backing-field property per auto-property, each raising the event.
/// </summary>
internal static partial class VbRefactoringTail
{
    public static async Task<object> ImplementInpcAsync(IRoslynWorkspace workspace, string className,
        string[]? propertyNames, bool dryRun, CancellationToken cancellationToken)
    {
        var solution = await workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No solution could be auto-located from the working directory." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(solution,
            name => name.Equals(className, StringComparison.OrdinalIgnoreCase), SymbolFilter.Type);

        var symbol = candidates.OfType<INamedTypeSymbol>()
            .FirstOrDefault(candidate => candidate.TypeKind == TypeKind.Class);
        if (symbol is null) return new { error = $"Class '{className}' not found." };

        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference?.SyntaxTree.FilePath is null)
            return new { error = $"'{className}' has no source declaration." };

        var document = VbRefactoringSupport.DocumentAt(solution, reference.SyntaxTree.FilePath);
        if (document is null) return new { error = $"Could not locate the document of '{className}'." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read {Path.GetFileName(document.FilePath)}." };

        var typeBlock = root.DescendantNodes().OfType<TypeBlockSyntax>()
            .FirstOrDefault(block => block.Span.Contains(reference.Span));
        if (typeBlock is null) return new { error = $"Could not locate the class declaration of '{className}'." };

        var autoProperties = typeBlock.Members.OfType<PropertyStatementSyntax>()
            .Where(statement => !statement.Modifiers.Any(token => token.IsKind(SyntaxKind.DefaultKeyword)))
            .Where(statement => propertyNames is null ||
                                propertyNames.Contains(statement.Identifier.ValueText, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (autoProperties.Count == 0)
            return new
            {
                error = propertyNames is null
                    ? $"'{className}' has no auto-property to convert."
                    : $"None of the requested properties is an auto-property of '{className}'.",
            };

        foreach (var property in autoProperties)
        {
            if (string.IsNullOrWhiteSpace(TypeTextOf(property.AsClause)))
                return new { error = $"'{property.Identifier.ValueText}' has no As <type> clause." };

            var backingName = "_" + char.ToLowerInvariant(property.Identifier.ValueText[0]) +
                              property.Identifier.ValueText[1..];
            if (symbol.GetMembers(backingName).Any(member => !member.IsImplicitlyDeclared))
                return new { error = $"'{className}' already has a backing field for '{property.Identifier.ValueText}'." };
        }

        var implementsInpc = symbol.AllInterfaces.Any(interfaceSymbol => interfaceSymbol.Name == "INotifyPropertyChanged");
        var hasEvent = symbol.GetMembers("PropertyChanged").OfType<IEventSymbol>().Any();
        var hasHelper = symbol.GetMembers().OfType<IMethodSymbol>()
            .Any(method => method.Name is "OnPropertyChanged" or "RaisePropertyChanged");

        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var indentation = IndentationOf(text, typeBlock.SpanStart) + "    ";

        var header = new System.Text.StringBuilder();
        if (!implementsInpc)
        {
            header.Append(indentation).Append("Implements System.ComponentModel.INotifyPropertyChanged")
                .Append(newLine).Append(newLine);
        }

        if (!hasEvent)
        {
            header.Append(indentation)
                .Append("Public Event PropertyChanged As System.ComponentModel.PropertyChangedEventHandler")
                .Append(" Implements System.ComponentModel.INotifyPropertyChanged.PropertyChanged")
                .Append(newLine).Append(newLine);
        }

        if (!hasHelper)
        {
            header.Append(indentation)
                .Append("Protected Sub OnPropertyChanged(<System.Runtime.CompilerServices.CallerMemberName> Optional propertyName As String = Nothing)")
                .Append(newLine)
                .Append(indentation).Append("    RaiseEvent PropertyChanged(Me, New System.ComponentModel.PropertyChangedEventArgs(propertyName))")
                .Append(newLine)
                .Append(indentation).Append("End Sub")
                .Append(newLine).Append(newLine);
        }

        var edits = new List<TextEdit>
        {
            new(document, new TextSpan(AfterTypeHeader(text, typeBlock), 0), header.ToString()),
        };

        foreach (var property in autoProperties)
        {
            var name = property.Identifier.ValueText;
            var typeText = TypeTextOf(property.AsClause)!;
            var backing = "_" + char.ToLowerInvariant(name[0]) + name[1..];
            var modifiers = string.Join(" ", property.Modifiers.Select(token => token.Text));
            if (modifiers.Length > 0) modifiers += " ";

            var readOnly = property.Modifiers.Any(token => token.IsKind(SyntaxKind.ReadOnlyKeyword));
            var writeOnly = property.Modifiers.Any(token => token.IsKind(SyntaxKind.WriteOnlyKeyword));

            var replacement = new System.Text.StringBuilder();
            replacement.Append(indentation).Append("Private ").Append(backing).Append(" As ").Append(typeText)
                .Append(newLine).Append(newLine);
            replacement.Append(indentation).Append(modifiers).Append("Property ").Append(name)
                .Append(" As ").Append(typeText).Append(newLine);

            if (!writeOnly)
            {
                replacement.Append(indentation).Append("    Get").Append(newLine)
                    .Append(indentation).Append("        Return ").Append(backing).Append(newLine)
                    .Append(indentation).Append("    End Get").Append(newLine);
            }

            if (!readOnly)
            {
                replacement.Append(indentation).Append("    Set(value As ").Append(typeText).Append(')').Append(newLine)
                    .Append(indentation).Append("        ").Append(backing).Append(" = value").Append(newLine)
                    .Append(indentation).Append("        OnPropertyChanged()").Append(newLine)
                    .Append(indentation).Append("    End Set").Append(newLine);
            }

            replacement.Append(indentation).Append("End Property").Append(newLine);

            edits.Add(new TextEdit(document, WholeLines(text, property.Span), replacement.ToString()));
        }

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken,
            "implementing INotifyPropertyChanged");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "implement_inpc",
            type = symbol.ToDisplayString(),
            properties = autoProperties.Select(property => property.Identifier.ValueText).ToList(),
            alreadyImplemented = implementsInpc,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }
}
