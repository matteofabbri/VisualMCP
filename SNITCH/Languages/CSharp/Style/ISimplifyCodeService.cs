namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// The C# entry point the MCP tool calls. The pipeline behind it is language-neutral
/// (<see cref="Snitch.Languages.Style.StyleRewriteService"/>); this interface is what keeps the tool and
/// its dependency injection independent of which languages exist.
/// </summary>
public interface ISimplifyCodeService
{
    Task<object> SimplifyAsync(
        string scope,
        string? filePath,
        string? projectName,
        string? rules,
        bool dryRun,
        CancellationToken cancellationToken = default);
}
