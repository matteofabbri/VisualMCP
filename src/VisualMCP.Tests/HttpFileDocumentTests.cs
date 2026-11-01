using Snitch.Runtime;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// http_file: the inspector reads the same parser run_http_file executes, so the rules are asserted
/// against the format itself, and the two edits are asserted against the file's own line endings.
/// </summary>
public sealed class HttpFileDocumentTests
{
    private const string Valid = """
        @baseUrl = http://localhost:5000
        @token = s3cret

        ### list things
        # @name listThings
        GET {{baseUrl}}/things
        Authorization: Bearer {{token}}

        ### create a thing
        POST {{baseUrl}}/things
        Content-Type: application/json

        { "name": "{{token}}" }
        """;

    [Fact]
    public void A_valid_file_has_no_finding()
    {
        var result = Inspect(Valid);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.VariableCount);
        Assert.Equal(2, result.RequestCount);
        Assert.Empty(result.Diagnostics!);
        Assert.Equal(new[] { "baseUrl", "token" }, result.Variables!.Select(variable => variable.Name));
        Assert.Equal(1, result.Variables![0].Line);
        Assert.Equal(6, result.Requests![0].Line);
        Assert.Equal("listThings", result.Requests[0].Name);
    }

    [Fact]
    public void A_block_with_no_request_line_is_an_error_on_its_own_line()
    {
        var result = Inspect("""
            @baseUrl = http://localhost

            ### broken
            http://localhost/things
            """);

        var finding = Assert.Single(result.Diagnostics!);
        Assert.Equal("HTTP0001", finding.RuleId);
        Assert.Equal(4, finding.Line);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void An_unknown_method_is_a_warning()
    {
        var result = Inspect("RETRIEVE http://localhost/things");

        var finding = Assert.Single(result.Diagnostics!);
        Assert.Equal("HTTP0002", finding.RuleId);
        Assert.Equal("warning", finding.Severity);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void An_undeclared_placeholder_is_reported_where_it_is_used()
    {
        var result = Inspect("""
            @baseUrl = http://localhost

            GET {{baseUrl}}/things/{{id}}
            """);

        var finding = Assert.Single(result.Diagnostics!);
        Assert.Equal("HTTP0003", finding.RuleId);
        Assert.Contains("'id'", finding.Message);
        Assert.Equal(3, finding.Line);
        Assert.Equal(["id"], result.Requests![0].UndefinedVariables.ToArray());
    }

    [Fact]
    public void A_dynamic_variable_is_not_an_undeclared_one()
    {
        var result = Inspect("GET http://localhost/x?nonce={{$guid}}");

        Assert.Empty(result.Diagnostics!);
    }

    [Fact]
    public void A_variable_declared_twice_and_a_name_used_twice_are_reported()
    {
        var result = Inspect("""
            @baseUrl = http://localhost
            @baseUrl = http://localhost:5000

            ### one
            # @name same
            GET {{baseUrl}}/a

            ### two
            # @name same
            GET {{baseUrl}}/b
            """);

        Assert.Equal(new[] { "HTTP0004", "HTTP0005" }, result.Diagnostics!.Select(finding => finding.RuleId));
        Assert.Equal(2, result.Diagnostics![0].Line);
        Assert.Equal(9, result.Diagnostics![1].Line);
    }

    [Fact]
    public void A_file_with_no_request_is_an_error()
    {
        var result = Inspect("@baseUrl = http://localhost");

        var finding = Assert.Single(result.Diagnostics!);
        Assert.Equal("HTTP0007", finding.RuleId);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Appending_a_request_keeps_the_file_line_endings_and_the_inspector_sees_it()
    {
        using var file = new HttpFile("""
            @baseUrl = http://localhost
            """ + "\r\n");

        var appended = await new HttpFileDocumentService().AppendAsync(
            file.Path,
            "create",
            "post",
            "{{baseUrl}}/things",
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            "{ \"name\": \"x\" }",
            dryRun: false);

        Assert.True(appended.Applied);
        Assert.True(appended.Changed);
        Assert.Contains("POST {{baseUrl}}/things", appended.Preview);

        var text = await File.ReadAllTextAsync(file.Path);
        Assert.Contains("\r\n", text);
        Assert.DoesNotContain("\n\n\n", text);

        var inspected = new HttpFileDocumentService().Inspect(file.Path);
        Assert.Equal(1, inspected.RequestCount);
        Assert.Equal("create", inspected.Requests![0].Name);
        Assert.Equal("POST", inspected.Requests[0].Method);
        Assert.Empty(inspected.Diagnostics!);
    }

    [Fact]
    public async Task A_dry_run_does_not_write()
    {
        using var file = new HttpFile("GET http://localhost/a\n");
        var before = await File.ReadAllTextAsync(file.Path);

        var result = await new HttpFileDocumentService().AppendAsync(
            file.Path, null, "GET", "http://localhost/b", null, null, dryRun: true);

        Assert.False(result.Applied);
        Assert.Equal(before, await File.ReadAllTextAsync(file.Path));
    }

    [Fact]
    public async Task An_unknown_method_is_refused_instead_of_written()
    {
        using var file = new HttpFile("GET http://localhost/a\n");

        var result = await new HttpFileDocumentService().AppendAsync(
            file.Path, null, "FETCH", "http://localhost/b", null, null, dryRun: false);

        Assert.NotNull(result.Error);
        Assert.Contains("FETCH", result.Error);
    }

    [Fact]
    public async Task Setting_a_variable_replaces_an_existing_declaration_or_inserts_one()
    {
        using var file = new HttpFile("""
            @baseUrl = http://localhost

            GET {{baseUrl}}/a
            """);

        var replaced = await new HttpFileDocumentService()
            .SetVariableAsync(file.Path, "baseUrl", "http://localhost:5000", dryRun: false);
        Assert.True(replaced.Changed);
        Assert.Equal(1, replaced.Line);
        Assert.Contains("@baseUrl = http://localhost:5000", await File.ReadAllTextAsync(file.Path));

        var inserted = await new HttpFileDocumentService()
            .SetVariableAsync(file.Path, "@token", "s3cret", dryRun: false);
        Assert.Equal(2, inserted.Line);

        var inspected = new HttpFileDocumentService().Inspect(file.Path);
        Assert.Equal(2, inspected.VariableCount);
        Assert.Empty(inspected.Diagnostics!);
    }

    private static HttpFileResult Inspect(string text)
    {
        using var file = new HttpFile(text);
        return new HttpFileDocumentService().Inspect(file.Path);
    }

    /// <summary>A <c>.http</c> file in its own temporary folder, deleted with the test.</summary>
    private sealed class HttpFile : IDisposable
    {
        private readonly string _directory;

        public HttpFile(string contents)
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vmcp-httpdoc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "requests.http");
            System.IO.File.WriteAllText(Path, contents);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
        }
    }
}
