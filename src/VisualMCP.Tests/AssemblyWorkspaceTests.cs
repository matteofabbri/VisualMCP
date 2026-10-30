using System.Diagnostics;
using Snitch.Metacode.Decompilation;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The decompiled assembly as a workspace: a small library is built once, decompiled into a Roslyn
/// solution and then searched, read, referenced and outlined — the navigation that text
/// decompilation could not offer.
/// </summary>
public sealed class AssemblyWorkspaceTests
{
    private const string LibrarySource = """
        namespace Demo;

        public sealed class Calculator
        {
            public int Add(int a, int b) => a + b;

            public int Twice(int a) => Add(a, a);
        }

        public static class CheckParameterSafety
        {
            public static string Describe(int value) => value > 0 ? "positive" : "other";
        }
        """;

    private static readonly Task<(string Directory, string Assembly)> FixtureTask = BuildFixtureAsync();

    [Fact]
    public async Task AssemblyWorkspace_LoadsSearchesReadsReferencesAndOutlines()
    {
        var (directory, assembly) = await FixtureTask;
        try
        {
            var service = new AssemblyWorkspaceService();

            var load = ResultAssert.Json(await service.LoadAsync(assembly));
            Assert.Equal("Demo", load.GetProperty("name").GetString());
            Assert.Equal(2, load.GetProperty("types").GetInt32());
            Assert.False(load.GetProperty("reused").GetBoolean());

            // CamelHumps: the uppercase letters find the type.
            var humps = ResultAssert.Json(await service.SearchAsync(assembly, "CPS", 10));
            Assert.Contains(humps.GetProperty("matches").EnumerateArray(),
                match => match.GetProperty("name").GetString() == "CheckParameterSafety");

            var byName = ResultAssert.Json(await service.SearchAsync(assembly, "Add", 10));
            Assert.Contains(byName.GetProperty("matches").EnumerateArray(),
                match => match.TryGetProperty("name", out var matchName) && matchName.GetString() == "Add" &&
                         match.TryGetProperty("owner", out var owner) && owner.GetString() == "Calculator");

            var source = ResultAssert.Json(await service.SourceAsync(assembly, "Demo.Calculator.Add", 5000));
            Assert.Contains("public int Add", source.GetProperty("code").GetString());
            Assert.True(source.GetProperty("line").GetInt32() > 0);

            var typeSource = ResultAssert.Json(await service.SourceAsync(assembly, "Demo.Calculator", 5000));
            Assert.Contains("Twice", typeSource.GetProperty("code").GetString());

            var references = ResultAssert.Json(await service.ReferencesAsync(assembly, "Demo.Calculator.Add", 20));
            Assert.True(references.GetProperty("referenceCount").GetInt32() >= 1,
                "the call in Twice is a use inside the decompiled set: " + references.GetRawText());

            var outline = ResultAssert.Json(await service.OutlineAsync(assembly, "Demo.Calculator", 50));
            Assert.Equal("Calculator", outline.GetProperty("type").GetString());
            Assert.Contains(outline.GetProperty("members").EnumerateArray(),
                member => member.GetProperty("name").GetString() == "Twice");

            var all = ResultAssert.Json(await service.OutlineAsync(assembly, null, 50));
            Assert.Equal(2, all.GetProperty("typeCount").GetInt32());

            // The second load reuses the workspace: the module version id is the key.
            var again = ResultAssert.Json(await service.LoadAsync(assembly));
            Assert.True(again.GetProperty("reused").GetBoolean());

            var unload = ResultAssert.Json(service.Unload(assembly));
            Assert.Equal(1, unload.GetProperty("unloaded").GetInt32());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static async Task<(string Directory, string Assembly)> BuildFixtureAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vmcp-asmws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(Path.Combine(directory, "Demo.cs"), LibrarySource);
        await File.WriteAllTextAsync(Path.Combine(directory, "Demo.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        foreach (var argument in new[] { "build", Path.Combine(directory, "Demo.csproj"), "-c", "Debug" })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, output);

        var assembly = Directory
            .EnumerateFiles(Path.Combine(directory, "bin"), "Demo.dll", SearchOption.AllDirectories)
            .First();

        return (directory, assembly);
    }
}
