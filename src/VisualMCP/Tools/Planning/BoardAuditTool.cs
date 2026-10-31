using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;
using VisualMCP.Analysis;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardAuditTool
{
    [McpServerTool(Name = "board_audit"), Description(
        "Which open cards are ALREADY SHIPPED. Every card that is not done is cross-checked against THIS build: the " +
        "tool names and context-action ids it mentions are matched against the live catalogue (a tool named after its " +
        "method counts under both spellings, FindSymbol and find_symbol), and the file paths it mentions are checked on " +
        "disk. A card whose named tools all exist is a closure CANDIDATE with the evidence; a card naming something " +
        "this build does not have is 'partly-shipped', listing the names that are absent — so a renamed capability " +
        "shows up instead of hiding. One non-capability name per four shipped tools is tolerated (a card quotes " +
        ".editorconfig keys and table names too), and only file-shaped paths are checked. Nothing is written unless " +
        "close=true, which moves the candidates to done and records the evidence as a comment. Run it when the board " +
        "looks stale, and before answering \"what is missing\" from it.")]
    public static Task<object> BoardAudit(
        IBoardService board,
        [Description("Move the closure candidates to done, with the evidence recorded as a comment (default: report only).")]
        bool close = false,
        [Description("Author recorded on the closing comment (default: board_audit).")]
        string? author = null)
        => board.AuditAsync(ToolCatalogue.KnownNames(), close, author);
}
