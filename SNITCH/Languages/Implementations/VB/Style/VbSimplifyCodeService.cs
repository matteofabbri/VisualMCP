using Snitch.Languages.Style;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Style;

/// <summary>
/// The Visual Basic entry point the MCP tool will call. The pipeline behind it is language-neutral
/// (<see cref="StyleRewriteService"/>); this interface is what keeps the tool and its dependency
/// injection independent of which languages exist, exactly as the C# one does.
/// </summary>
public interface IVbSimplifyCodeService
{
    Task<object> SimplifyAsync(
        string scope,
        string? filePath,
        string? projectName,
        string? rules,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// VB on top of the shared pipeline: the only VB-specific wiring is the rule set, the language adapter
/// and the name the report uses. Everything behind it — scope selection, the passes, the compiler gate,
/// the report, the edit history — is <see cref="StyleRewriteService"/>.
/// </summary>
public sealed class VbSimplifyCodeService : StyleRewriteService, IVbSimplifyCodeService
{
    public VbSimplifyCodeService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
        : base(workspace, store, VbStyleRuleSet.Default, VbStyleLanguage.Default, "simplify_vb_code")
    {
    }

    /// <summary>Every rule id the VB engine can apply.</summary>
    public static new IReadOnlyList<StyleRule> Rules => VbStyleRewriteCatalog.Supported;

    /// <summary>The VB ids this engine does not rewrite, with the reason.</summary>
    public static new IReadOnlyList<(string Id, string Reason)> NotRewritten => VbStyleRewriteCatalog.NotRewritten;
}
