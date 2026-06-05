using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using VisualMCP.Implementation.Execution;

namespace VisualMCP.Implementation.Decompilation;

/// <summary>Decompilation backed by ICSharpCode.Decompiler (the ILSpy engine).</summary>
internal static class DecompileImpl
{
    private static CSharpDecompiler Create(string assemblyPath) =>
        new(Path.GetFullPath(assemblyPath), new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });

    internal static object DecompileType(string assemblyPath, string typeFullName, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return new { error = $"Assembly not found: {assemblyPath}" };
        if (string.IsNullOrWhiteSpace(typeFullName))
            return new { error = "A fully-qualified type name is required (e.g. 'Namespace.TypeName')." };

        try
        {
            var decompiler = Create(assemblyPath);
            var code = decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));
            return new
            {
                assembly = Path.GetFileName(assemblyPath),
                type = typeFullName,
                language = "C#",
                truncated = code.Length > maxChars,
                totalChars = code.Length,
                code = ProcessRunner.Truncate(code, maxChars),
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to decompile type '{typeFullName}': {ex.Message}", assemblyPath };
        }
    }

    internal static object DecompileAssembly(string assemblyPath, string? outputPath, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return new { error = $"Assembly not found: {assemblyPath}" };

        try
        {
            var decompiler = Create(assemblyPath);
            var code = decompiler.DecompileWholeModuleAsString();

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                var full = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, code);
                return new { assembly = Path.GetFileName(assemblyPath), outputPath = full, bytes = code.Length, note = "Whole module decompiled and written to file." };
            }

            return new
            {
                assembly = Path.GetFileName(assemblyPath),
                language = "C#",
                truncated = code.Length > maxChars,
                totalChars = code.Length,
                note = code.Length > maxChars ? "Large output truncated — pass outputPath to write the full C# to a file, or use decompile_type for one type." : null,
                code = ProcessRunner.Truncate(code, maxChars),
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to decompile assembly: {ex.Message}", assemblyPath };
        }
    }
}
