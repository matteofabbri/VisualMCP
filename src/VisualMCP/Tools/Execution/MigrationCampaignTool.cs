using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class MigrationCampaignTool
{
    [McpServerTool(Name = "migration_campaign"), Description(
        "Apply ONE named rewrite across a solution or a project — the feature-level migration no " +
        "per-declaration tool does. Campaigns: 'nullable' adds a '#nullable enable' header to every C# file " +
        "that has no #nullable directive; 'file-scoped-namespaces' turns a file's single block namespace " +
        "into a file-scoped one (members de-indented by the Roslyn formatter); 'braces' wraps every " +
        "brace-less if/else/for/foreach/while/do/using/lock/fixed body in a block; 'primary-constructors' " +
        "converts every type whose single constructor only assigns parameters to fields. Every edit is " +
        "compiled before anything is written: the project is gated as a whole and, when that gate fails, " +
        "each file is gated on its own so the report names exactly what was refused and why. A nullable " +
        "campaign accepts a file that compiles but adds nullability warnings — the warnings are the debt it " +
        "creates, not a compile error. preview by default (dryRun=true): the report carries a short diff per " +
        "file and nothing is written.")]
    public static Task<MigrationCampaignResult> MigrationCampaign(
        IMigrationCampaignService campaigns,
        [Description("nullable | file-scoped-namespaces | braces | primary-constructors.")]
        string campaign,
        [Description("Only this project (exact name from the loaded solution); omit for the whole solution.")]
        string? projectName = null,
        [Description("Preview only (default true). Pass false to write the accepted edits.")]
        bool dryRun = true)
        => campaigns.RunAsync(campaign, projectName, dryRun);
}
