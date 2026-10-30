using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindSecurityIssuesTool
{
    [McpServerTool, Description(
        "Hunt for common security vulnerabilities across the solution using Roslyn's semantic model, " +
        "returning CWE-tagged findings with a severity. Detects: SQL injection (CWE-89), OS command " +
        "injection via Process.Start/ProcessStartInfo (CWE-78), weak crypto algorithms MD5/SHA1/DES/RC2 " +
        "(CWE-327) and ECB cipher mode, insecure System.Random for security (CWE-338), hardcoded " +
        "secrets (CWE-798), unsafe deserialization incl. BinaryFormatter and Newtonsoft TypeNameHandling " +
        "(CWE-502), SSRF from user-controlled URLs (CWE-918), XXE via DtdProcessing/XmlResolver (CWE-611), " +
        "disabled TLS certificate validation (CWE-295), and path traversal (CWE-22). " +
        "Use this INSTEAD OF grepping for risky APIs — Roslyn resolves types precisely (e.g. System.Random " +
        "vs a custom Random) and follows the data flow text search cannot. The solution auto-loads on first use.")]
    public static Task<object> FindSecurityIssues(
        IFindSecurityIssuesService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null)
        => svc.FindSecurityIssues(projectName);
}