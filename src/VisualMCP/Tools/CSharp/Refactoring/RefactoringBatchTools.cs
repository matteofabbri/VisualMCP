using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

/// <summary>
/// The refactoring batch added with the gap-closing pass: inline a field, turn an extension method
/// back into a static one, safe-delete a file or folder, move members to a partial file, invert a
/// boolean. Every one is compiler-gated and previews by default.
/// </summary>
[McpServerToolType]
public static class RefactoringBatchTools
{
    [McpServerTool(Name = "inline_field"), Description(
        "Inline a field at every use and remove it (Rider's 'Inline field'). Accepted only when the field " +
        "has an initializer, is declared in ONE file and is never written outside its declaration; a use in " +
        "another file is refused (the initializer would be pasted where its context is not visible). The " +
        "initializer is re-evaluated at each use afterwards, as the note in the result says. Compiler-gated, " +
        "preview by default.")]
    public static Task<object> InlineField(
        IInlineFieldService svc,
        [Description("File that declares the field.")] string filePath,
        [Description("Field name.")] string fieldName,
        [Description("Containing type name, when the file declares several types with that field.")] string? containingType = null,
        [Description("Preview only (default: true).")] bool dryRun = true)
        => svc.InlineField(filePath, fieldName, containingType, dryRun, CancellationToken.None);

    [McpServerTool(Name = "extension_to_static"), Description(
        "Turn an extension method back into a static one (the inverse of static_to_extension): the 'this' " +
        "modifier goes away and every x.M(a) becomes Type.M(x, a) across the solution. A use that is not a " +
        "plain call (a method group) is refused, because there is no receiver to move into the argument list. " +
        "Compiler-gated, preview by default.")]
    public static Task<object> ExtensionToStatic(
        IExtensionToStaticService svc,
        [Description("File that declares the extension method.")] string filePath,
        [Description("Method name.")] string methodName,
        [Description("Containing type name, when several types in that file declare it.")] string? containingType = null,
        [Description("Preview only (default: true).")] bool dryRun = true)
        => svc.Convert(filePath, methodName, containingType, dryRun, CancellationToken.None);

    [McpServerTool(Name = "safe_delete_file"), Description(
        "Delete a source file only when nothing outside it uses what it declares. It refuses, listing the " +
        "uses with file:line, when a symbol leaks out; nothing is written in that case. The file is removed " +
        "from disk and from the Roslyn model. Preview by default: deletion is destructive.")]
    public static Task<object> SafeDeleteFile(
        ISafeDeleteFileService svc,
        [Description("The .cs file to delete.")] string filePath,
        [Description("Preview only (default: true).")] bool dryRun = true)
        => svc.DeleteFile(filePath, dryRun, CancellationToken.None);

    [McpServerTool(Name = "safe_delete_folder"), Description(
        "Delete a folder of C# files only when nothing outside it uses their declarations. It refuses when " +
        "the folder holds anything but .cs files (a reference check cannot judge those), and when a symbol " +
        "leaks out. Preview by default: deletion is destructive.")]
    public static Task<object> SafeDeleteFolder(
        ISafeDeleteFileService svc,
        [Description("The folder to delete.")] string folderPath,
        [Description("Preview only (default: true).")] bool dryRun = true)
        => svc.DeleteFolder(folderPath, dryRun, CancellationToken.None);

    [McpServerTool(Name = "extract_members_to_partial"), Description(
        "Move members of a type into a new partial file of the same type (Rider's 'Extract members to " +
        "partial'): the type is marked partial in every declaration and the members move to " +
        "<Type>.<Suffix>.cs with the file's usings and namespace form. Generic types are refused (the partial " +
        "would have to repeat the constraints). Compiler-gated; both files are previewed and written together.")]
    public static Task<object> ExtractMembersToPartial(
        IExtractMembersToPartialService svc,
        [Description("File that declares the type.")] string filePath,
        [Description("The type whose members move.")] string typeName,
        [Description("Members to move: method/property/field/event/nested type names.")] string[] memberNames,
        [Description("Suffix for the new file name (default: 'Partial').")] string? suffix = null,
        [Description("Preview only (default: true).")] bool dryRun = true)
        => svc.Extract(filePath, typeName, memberNames, suffix, dryRun, CancellationToken.None);

    [McpServerTool(Name = "invert_boolean"), Description(
        "Invert a bool-returning member (Rider's 'Invert Boolean'): every return or the expression body is " +
        "negated — true/false flip, a negated expression unwraps, anything else gets !(…) — and with newName " +
        "the member is renamed across the solution. Refused when the body has a 'return;' or yields. " +
        "Compiler-gated, preview by default.")]
    public static Task<object> InvertBoolean(
        IInvertBooleanService svc,
        [Description("File that declares the member.")] string filePath,
        [Description("Member name (a method returning bool).")] string memberName,
        [Description("Containing type name, when ambiguous.")] string? containingType = null,
        [Description("New name after the inversion, e.g. IsNotReady for IsReady.")] string? newName = null,
        [Description("Preview only (default: true).")] bool dryRun = true)
        => svc.Invert(filePath, memberName, containingType, newName, dryRun, CancellationToken.None);
}
