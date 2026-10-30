using System.Text.Json;
using ModelContextProtocol;
using Snitch.IO;
using Snitch.Languages.CSharp.Parsing.Model;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The typed results of <see cref="IIoService"/> are what the IO tools return to the model, so
/// their JSON has to stay what it always was: camelCase members, a null member omitted, and a
/// failed call reduced to <c>{"error":"…"}</c>. These facts pin that contract, which the type
/// system alone cannot guarantee — it depends on the serializer defaults the MCP server uses.
/// </summary>
public sealed class IoResultSerializationTests
{
    private static JsonElement Json(object result)
    {
        // Same options and same static type the MCP server serializes a tool result with.
        string json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void A_failed_call_serializes_to_the_error_alone()
    {
        JsonElement root = Json(new MovePathResult { Error = "Source not found: C:\\x" });

        JsonProperty only = Assert.Single(root.EnumerateObject());
        Assert.Equal("error", only.Name);
    }

    [Fact]
    public void A_failed_read_leaves_the_line_counts_unset()
    {
        JsonElement root = Json(new ReadFileResult { Path = @"C:\x.cs", Error = "File not found: C:\\x.cs" });

        Assert.Equal("File not found: C:\\x.cs", root.GetProperty("error").GetString());
        Assert.False(root.TryGetProperty("totalLines", out _));
        Assert.False(root.TryGetProperty("returnedLines", out _));
        Assert.False(root.TryGetProperty("content", out _));
    }

    [Fact]
    public void A_loaded_solution_reports_its_projects_in_camel_case()
    {
        JsonElement root = Json(new LoadSolutionResult
        {
            Solution = "MyApp.sln",
            Directory = @"C:\src",
            ProjectCount = 1,
            WorkspaceDiagnostics = Array.Empty<string>(),
            Projects = new[]
            {
                new LoadedProject("MyApp", @"C:\src\MyApp\MyApp.csproj", "C#", "MyApp", 12, 1, 40),
            },
        });

        Assert.Equal("MyApp.sln", root.GetProperty("solution").GetString());
        Assert.Equal(1, root.GetProperty("projectCount").GetInt32());
        Assert.Equal(0, root.GetProperty("workspaceDiagnostics").GetArrayLength());
        Assert.False(root.TryGetProperty("error", out _));

        JsonElement project = root.GetProperty("projects")[0];
        Assert.Equal("MyApp", project.GetProperty("name").GetString());
        Assert.Equal(12, project.GetProperty("documentCount").GetInt32());
        Assert.Equal(40, project.GetProperty("metadataReferenceCount").GetInt32());
    }

    [Fact]
    public void Check_solution_keeps_the_lowercase_csharp_count_name()
    {
        JsonElement loaded = Json(new CheckSolutionResult
        {
            IsLoaded = true,
            SolutionPath = @"C:\src\MyApp.sln",
            CSharpProjectCount = 3,
            Message = "ok",
        });

        Assert.Equal(3, loaded.GetProperty("csharpProjectCount").GetInt32());
        Assert.False(loaded.TryGetProperty("cSharpProjectCount", out _));

        JsonElement empty = Json(new CheckSolutionResult { IsLoaded = false, Message = "no solution" });
        Assert.False(empty.TryGetProperty("csharpProjectCount", out _));
    }

    [Fact]
    public void A_file_listing_carries_the_file_members_and_no_directory_members()
    {
        JsonElement root = Json(new ListDirectoryResult
        {
            Path = @"C:\bin\app.dll",
            Exists = true,
            Type = "file",
            SizeBytes = 2048,
            SizeMB = 0.0,
            LastModified = "2026-10-29 10:00:00",
        });

        Assert.Equal(2048, root.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("2026-10-29 10:00:00", root.GetProperty("lastModified").GetString());
        Assert.True(root.TryGetProperty("sizeMB", out _));
        Assert.False(root.TryGetProperty("pattern", out _));
        Assert.False(root.TryGetProperty("files", out _));
    }

    [Fact]
    public void A_directory_listing_keeps_the_mb_suffixes_and_the_entry_shape()
    {
        JsonElement root = Json(new ListDirectoryResult
        {
            Path = @"C:\src",
            Exists = true,
            Type = "directory",
            Pattern = "*.cs",
            Recursive = false,
            MatchedFileCount = 1,
            MatchedTotalSizeBytes = 4096,
            MatchedTotalSizeMB = 0.0,
            ReturnedCount = 1,
            Truncated = false,
            Subdirectories = Array.Empty<string>(),
            Files = new[] { new DirectoryFileEntry("a.cs", "a.cs", 100, 0.0, null) },
        });

        Assert.Equal(1, root.GetProperty("matchedFileCount").GetInt32());
        Assert.Equal("*.cs", root.GetProperty("pattern").GetString());
        Assert.Equal(4096, root.GetProperty("matchedTotalSizeBytes").GetInt64());
        Assert.True(root.TryGetProperty("matchedTotalSizeMB", out _));
        Assert.False(root.TryGetProperty("sizeBytes", out _));

        JsonElement file = root.GetProperty("files")[0];
        Assert.Equal("a.cs", file.GetProperty("relativePath").GetString());
        Assert.True(file.TryGetProperty("sizeMB", out _));
        Assert.False(file.TryGetProperty("lastModified", out _));
    }

    [Fact]
    public void A_project_info_lists_packages_with_the_shared_package_reference_shape()
    {
        JsonElement root = Json(new ProjectInfoResult
        {
            Name = "MyApp",
            DocumentCount = 3,
            SourceFiles = new[] { @"C:\src\MyApp\Program.cs" },
            PackageReferences = new[] { new PackageRef("xunit", "2.9.3") },
        });

        Assert.Equal("MyApp", root.GetProperty("name").GetString());
        Assert.Equal(3, root.GetProperty("documentCount").GetInt32());
        Assert.Equal(@"C:\src\MyApp\Program.cs", root.GetProperty("sourceFiles")[0].GetString());

        JsonElement package = root.GetProperty("packageReferences")[0];
        Assert.Equal("xunit", package.GetProperty("name").GetString());
        Assert.Equal("2.9.3", package.GetProperty("version").GetString());
    }

    [Fact]
    public void A_move_reports_what_it_did_and_still_omits_nothing_on_success()
    {
        JsonElement root = Json(new MovePathResult
        {
            Moved = true,
            Type = "file",
            Source = @"C:\a.txt",
            Destination = @"C:\b.txt",
        });

        Assert.True(root.GetProperty("moved").GetBoolean());
        Assert.Equal("file", root.GetProperty("type").GetString());
        Assert.Equal(@"C:\a.txt", root.GetProperty("source").GetString());
        Assert.False(root.TryGetProperty("error", out _));
    }
}
