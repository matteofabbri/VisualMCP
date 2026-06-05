using System.Text;
using System.Text.RegularExpressions;
using VisualMCP.Implementation.Execution;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.Benchmarking;

/// <summary>
/// Ad-hoc benchmarking: generates a temporary BenchmarkDotNet project around a
/// code snippet (optionally referencing a solution project) and runs it — so you
/// can measure any code without writing a [Benchmark]-annotated class yourself.
/// </summary>
internal static class QuickBenchmarkImpl
{
    private const string BenchmarkDotNetVersion = "0.14.0";

    internal static async Task<object> RunAsync(string code, string? setupCode, string[]? usings, string? referenceProject, string? targetFramework, bool quick, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new { error = "Provide the code statements to benchmark." };

        if (timeoutSeconds < 30) timeoutSeconds = 30;
        if (timeoutSeconds > 1800) timeoutSeconds = 1800;

        // Resolve an optional project to reference (for its types) and infer the TFM.
        string? referencedCsproj = null;
        var tfm = targetFramework;
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (!string.IsNullOrWhiteSpace(referenceProject) && solution is not null)
        {
            var proj = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, referenceProject, StringComparison.OrdinalIgnoreCase));
            if (proj?.FilePath is null)
                return new { error = $"Reference project '{referenceProject}' not found in the solution." };
            referencedCsproj = proj.FilePath;
            tfm ??= ReadTargetFramework(proj.FilePath);
        }
        tfm ??= "net10.0";

        var dir = Path.Combine(Path.GetTempPath(), $"vmcp-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            // .csproj
            var csproj = new StringBuilder();
            csproj.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            csproj.AppendLine("  <PropertyGroup>");
            csproj.AppendLine("    <OutputType>Exe</OutputType>");
            csproj.AppendLine($"    <TargetFramework>{tfm}</TargetFramework>");
            csproj.AppendLine("    <Nullable>disable</Nullable>");
            csproj.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
            csproj.AppendLine("    <Optimize>true</Optimize>");
            csproj.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            csproj.AppendLine("  </PropertyGroup>");
            csproj.AppendLine("  <ItemGroup>");
            csproj.AppendLine($"    <PackageReference Include=\"BenchmarkDotNet\" Version=\"{BenchmarkDotNetVersion}\" />");
            if (referencedCsproj is not null)
                csproj.AppendLine($"    <ProjectReference Include=\"{referencedCsproj}\" />");
            csproj.AppendLine("  </ItemGroup>");
            csproj.AppendLine("</Project>");
            await File.WriteAllTextAsync(Path.Combine(dir, "Bench.csproj"), csproj.ToString());

            // Program.cs
            var prog = new StringBuilder();
            prog.AppendLine("using BenchmarkDotNet.Attributes;");
            prog.AppendLine("using BenchmarkDotNet.Running;");
            if (usings is not null)
                foreach (var u in usings.Where(u => !string.IsNullOrWhiteSpace(u)))
                    prog.AppendLine($"using {u.Trim().TrimEnd(';')};");
            prog.AppendLine();
            prog.AppendLine("BenchmarkRunner.Run<__Bench>();");
            prog.AppendLine();
            prog.AppendLine("[MemoryDiagnoser]");
            if (quick) prog.AppendLine("[ShortRunJob]");
            prog.AppendLine("public class __Bench");
            prog.AppendLine("{");
            prog.AppendLine("    [GlobalSetup] public void __Setup()");
            prog.AppendLine("    {");
            if (!string.IsNullOrWhiteSpace(setupCode)) prog.AppendLine(setupCode);
            prog.AppendLine("    }");
            prog.AppendLine();
            prog.AppendLine("    [Benchmark] public void __Run()");
            prog.AppendLine("    {");
            prog.AppendLine(code);
            prog.AppendLine("    }");
            prog.AppendLine("}");
            await File.WriteAllTextAsync(Path.Combine(dir, "Program.cs"), prog.ToString());

            var (exitCode, timedOut, stdout, stderr, elapsed) =
                await ProcessRunner.RunAsync("dotnet", "run -c Release --project \"Bench.csproj\"", dir, timeoutSeconds);

            var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
            return new
            {
                tfm,
                referenceProject,
                quick,
                timedOut,
                exitCode = timedOut ? (int?)null : exitCode,
                durationMs = (long)elapsed.TotalMilliseconds,
                note = timedOut
                    ? $"Benchmark still running after {timeoutSeconds}s and was stopped."
                    : (exitCode == 0 ? "Benchmark completed." : "Process failed — see output (often a compile error in the snippet or a restore problem)."),
                output = Tail(combined, 20_000),
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Quick benchmark failed: {ex.Message}" };
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string? ReadTargetFramework(string csprojPath)
    {
        try
        {
            var xml = File.ReadAllText(csprojPath);
            var m = Regex.Match(xml, @"<TargetFramework>([^<]+)</TargetFramework>", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            var ms = Regex.Match(xml, @"<TargetFrameworks>([^<;]+)", RegexOptions.IgnoreCase);
            if (ms.Success) return ms.Groups[1].Value.Trim();
        }
        catch { }
        return null;
    }

    private static string Tail(string s, int max) =>
        s.Length <= max ? s : "…(head truncated)\n" + s[^max..];
}
