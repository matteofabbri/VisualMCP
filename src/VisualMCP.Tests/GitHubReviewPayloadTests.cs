using System.Text.Json;
using Snitch.GitHub;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The create-review payload: the part of an inline review that is easy to get wrong (line vs
/// position, side, event) is built and validated before anything is sent to GitHub.
/// </summary>
public sealed class GitHubReviewPayloadTests
{
    [Fact]
    public void BodyOnly_BecomesACommentReview()
    {
        var (payload, error) = GitHubService.BuildReviewPayload("looks good", null, null);

        Assert.Null(error);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("looks good", document.RootElement.GetProperty("body").GetString());
        Assert.Equal("COMMENT", document.RootElement.GetProperty("event").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("comments").GetArrayLength());
    }

    [Fact]
    public void ApproveAndRequestChanges_MapToTheirEvents()
    {
        Assert.Equal("APPROVE", EventOf(GitHubService.BuildReviewPayload("ok", "approve", null).Payload));
        Assert.Equal("REQUEST_CHANGES", EventOf(GitHubService.BuildReviewPayload("no", "request-changes", null).Payload));
        Assert.Equal("REQUEST_CHANGES", EventOf(GitHubService.BuildReviewPayload("no", "request_changes", null).Payload));
    }

    [Fact]
    public void LineComments_CarryPathLineAndSide()
    {
        var comments = """
            [
              { "path": "src/App.cs", "line": 42, "side": "RIGHT", "body": "this can throw" },
              { "path": "src/App.cs", "line": 10, "body": "left side defaults to RIGHT" },
              { "path": "src/App.cs", "position": 5, "body": "offset in the diff" }
            ]
            """;

        var (payload, error) = GitHubService.BuildReviewPayload(null, "comment", comments);
        Assert.Null(error);

        using var document = JsonDocument.Parse(payload);
        var array = document.RootElement.GetProperty("comments");
        Assert.Equal(3, array.GetArrayLength());

        Assert.Equal("src/App.cs", array[0].GetProperty("path").GetString());
        Assert.Equal(42, array[0].GetProperty("line").GetInt32());
        Assert.Equal("RIGHT", array[0].GetProperty("side").GetString());
        Assert.Equal("RIGHT", array[1].GetProperty("side").GetString());
        Assert.False(array[1].TryGetProperty("position", out _));
        Assert.Equal(5, array[2].GetProperty("position").GetInt32());
        Assert.False(array[2].TryGetProperty("line", out _));
    }

    [Fact]
    public void UnknownEvent_IsRefused()
    {
        var (_, error) = GitHubService.BuildReviewPayload("x", "merge-it", null);
        Assert.Contains("comment, approve or request-changes", error);
    }

    [Fact]
    public void CommentWithoutPath_NamesTheComment()
    {
        var (_, error) = GitHubService.BuildReviewPayload(null, null, """[{"line":4,"body":"x"}]""");
        Assert.Contains("comment 1 needs 'path'", error);
    }

    [Fact]
    public void CommentWithoutAnAnchor_IsRefused()
    {
        var (_, error) = GitHubService.BuildReviewPayload(null, null, """[{"path":"a.cs","body":"x"}]""");
        Assert.Contains("'line'", error);
    }

    [Fact]
    public void BadSide_IsRefused()
    {
        var (_, error) = GitHubService.BuildReviewPayload(null, null, """[{"path":"a.cs","line":1,"side":"MIDDLE","body":"x"}]""");
        Assert.Contains("RIGHT", error);
    }

    [Fact]
    public void NotAnArray_IsRefused()
    {
        var (_, error) = GitHubService.BuildReviewPayload(null, null, """{"path":"a.cs"}""");
        Assert.Contains("JSON array", error);
    }

    [Fact]
    public void EmptyReview_IsRefused()
    {
        var (_, error) = GitHubService.BuildReviewPayload(null, null, null);
        Assert.Contains("body or at least one line comment", error);
    }

    private static string EventOf(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("event").GetString()!;
    }
}
