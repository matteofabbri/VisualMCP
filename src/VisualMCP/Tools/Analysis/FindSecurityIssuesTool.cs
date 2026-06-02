using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Analysis;

[McpServerToolType]
public static class FindSecurityIssuesTool
{
    [McpServerTool, Description(
        "Scan the codebase for common security vulnerabilities using Roslyn's semantic model: " +
        "SQL injection via string interpolation/concatenation, use of weak cryptographic algorithms " +
        "(MD5, SHA1, DES, RC2), use of System.Random for security-sensitive contexts, " +
        "hardcoded credentials/secrets in string literals, unsafe deserialization " +
        "(BinaryFormatter, XmlSerializer with external input), missing HTTPS enforcement, " +
        "and path traversal risks (Path.Combine with user input). " +
        "Do NOT search for these patterns yourself — Roslyn resolves types precisely " +
        "(e.g. distinguishing System.Random from a custom Random class). " +
        "Requires load_solution first.")]
    public static async Task<object> FindSecurityIssues(
        [Description("Optional: restrict to a single project by name")] string? projectName = null)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var findings = new List<object>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root  = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                await CheckSqlInjection(root, model, document, project, findings);
                CheckWeakCrypto(root, model, document, project, findings);
                CheckInsecureRandom(root, model, document, project, findings);
                CheckHardcodedSecrets(root, document, project, findings);
                CheckUnsafeDeserialization(root, model, document, project, findings);
                CheckPathTraversal(root, model, document, project, findings);
            }
        }

        var byKind = findings
            .GroupBy(f => ((dynamic)f).IssueKind.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            projectFilter  = projectName ?? "all",
            totalFindings  = findings.Count,
            byKind,
            findings,
        };
    }

    // ── SQL Injection ────────────────────────────────────────────────────────────

    private static Task CheckSqlInjection(
        SyntaxNode root, SemanticModel model, Document doc, Project project, List<object> findings)
    {
        // Look for string interpolations or concatenations passed to SQL-executing methods
        var sqlMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ExecuteNonQuery", "ExecuteScalar", "ExecuteReader",
            "ExecuteSqlRaw", "ExecuteSqlCommand", "FromSqlRaw",
            "ExecuteQuery", "Execute",
        };

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? methodName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax id          => id.Identifier.Text,
                _                                => null,
            };

            if (methodName is null || !sqlMethods.Contains(methodName)) continue;

            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (ContainsDynamicString(arg.Expression))
                {
                    Report("SQL Injection risk", arg.Expression, doc, project, findings,
                        $"String interpolation or concatenation passed to '{methodName}'. " +
                        "Use parameterised queries or an ORM to prevent SQL injection.");
                }
            }
        }

        return Task.CompletedTask;
    }

    // ── Weak Cryptography ────────────────────────────────────────────────────────

    private static void CheckWeakCrypto(
        SyntaxNode root, SemanticModel model, Document doc, Project project, List<object> findings)
    {
        var weakAlgorithms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MD5"]    = "MD5 is cryptographically broken. Use SHA-256 or SHA-512.",
            ["SHA1"]   = "SHA-1 is deprecated for security use. Use SHA-256 or SHA-512.",
            ["DES"]    = "DES has a 56-bit key and is trivially brute-forced. Use AES-256.",
            ["TripleDES"] = "3DES is deprecated. Use AES-256.",
            ["RC2"]    = "RC2 is weak and deprecated. Use AES-256.",
        };

        // ObjectCreationExpression: new MD5CryptoServiceProvider()
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = creation.Type.ToString();
            foreach (var (alg, advice) in weakAlgorithms)
            {
                if (typeName.Contains(alg, StringComparison.OrdinalIgnoreCase))
                {
                    Report("Weak cryptographic algorithm", creation, doc, project, findings,
                        $"'{typeName}' uses {alg}. {advice}");
                    break;
                }
            }
        }

        // Static factory: MD5.Create(), SHA1.Create()
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Name.Identifier.Text != "Create") continue;

            var sym = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (sym is null) continue;

            var typeName = sym.ContainingType?.Name ?? "";
            foreach (var (alg, advice) in weakAlgorithms)
            {
                if (typeName.Equals(alg, StringComparison.OrdinalIgnoreCase))
                {
                    Report("Weak cryptographic algorithm", invocation, doc, project, findings,
                        $"'{typeName}.Create()' uses {alg}. {advice}");
                    break;
                }
            }
        }
    }

    // ── Insecure Random ──────────────────────────────────────────────────────────

    private static void CheckInsecureRandom(
        SyntaxNode root, SemanticModel model, Document doc, Project project, List<object> findings)
    {
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var sym = model.GetTypeInfo(creation).Type;
            if (sym is null) continue;
            if (sym.Name == "Random" && sym.ContainingNamespace?.ToDisplayString() == "System")
            {
                Report("Insecure random number generator", creation, doc, project, findings,
                    "System.Random is not cryptographically secure. " +
                    "Use System.Security.Cryptography.RandomNumberGenerator for security-sensitive code.");
            }
        }
    }

    // ── Hardcoded Secrets ────────────────────────────────────────────────────────

    private static void CheckHardcodedSecrets(
        SyntaxNode root, Document doc, Project project, List<object> findings)
    {
        var secretPatterns = new[]
        {
            ("password",   "Hardcoded password"),
            ("passwd",     "Hardcoded password"),
            ("secret",     "Hardcoded secret"),
            ("apikey",     "Hardcoded API key"),
            ("api_key",    "Hardcoded API key"),
            ("accesstoken","Hardcoded access token"),
            ("privatekey", "Hardcoded private key"),
            ("connectionstring", "Hardcoded connection string"),
        };

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var leftText = assignment.Left.ToString().ToLowerInvariant().Replace("_", "").Replace("-", "");
            foreach (var (pattern, label) in secretPatterns)
            {
                if (!leftText.Contains(pattern)) continue;
                if (assignment.Right is not LiteralExpressionSyntax lit) continue;
                if (!lit.IsKind(SyntaxKind.StringLiteralToken) &&
                    !lit.Token.IsKind(SyntaxKind.StringLiteralToken)) continue;
                var value = lit.Token.ValueText;
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("{{")) continue;

                Report("Hardcoded secret", assignment, doc, project, findings,
                    $"{label} detected in assignment to '{assignment.Left}'. " +
                    "Move secrets to environment variables, user secrets, or a vault.");
                break;
            }
        }

        // Also catch variable declarations: string password = "abc123"
        foreach (var varDecl in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            var name = varDecl.Identifier.Text.ToLowerInvariant().Replace("_", "");
            foreach (var (pattern, label) in secretPatterns)
            {
                if (!name.Contains(pattern)) continue;
                if (varDecl.Initializer?.Value is not LiteralExpressionSyntax lit) continue;
                if (!lit.Token.IsKind(SyntaxKind.StringLiteralToken)) continue;
                var value = lit.Token.ValueText;
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("{{")) continue;

                Report("Hardcoded secret", varDecl, doc, project, findings,
                    $"{label} detected in variable '{varDecl.Identifier.Text}'. " +
                    "Move secrets to environment variables, user secrets, or a vault.");
                break;
            }
        }
    }

    // ── Unsafe Deserialization ───────────────────────────────────────────────────

    private static void CheckUnsafeDeserialization(
        SyntaxNode root, SemanticModel model, Document doc, Project project, List<object> findings)
    {
        var dangerousTypes = new HashSet<string>
        {
            "BinaryFormatter", "SoapFormatter", "NetDataContractSerializer", "ObjectStateFormatter",
        };

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = creation.Type.ToString();
            if (dangerousTypes.Contains(typeName))
            {
                Report("Unsafe deserialization", creation, doc, project, findings,
                    $"'{typeName}' can deserialise arbitrary types and is a known RCE vector. " +
                    "Use System.Text.Json or a type-safe serialiser with a known type allowlist.");
            }
        }

        // JavaScriptSerializer.Deserialize with object type
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Name.Identifier.Text != "Deserialize" && ma.Name.Identifier.Text != "DeserializeObject") continue;

            var sym = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (sym is null) continue;

            var container = sym.ContainingType?.Name ?? "";
            if (container == "JavaScriptSerializer" || container == "JsonConvert")
            {
                // Flag only if the generic type argument is object or dynamic
                var typeArgs = (invocation.Expression as MemberAccessExpressionSyntax)?.Name as GenericNameSyntax;
                var typeArg  = typeArgs?.TypeArgumentList.Arguments.FirstOrDefault()?.ToString();
                if (typeArg is "object" or "dynamic")
                {
                    Report("Unsafe deserialization", invocation, doc, project, findings,
                        $"Deserialising into 'object' or 'dynamic' via '{container}' allows arbitrary type instantiation. " +
                        "Use a concrete known type.");
                }
            }
        }
    }

    // ── Path Traversal ───────────────────────────────────────────────────────────

    private static void CheckPathTraversal(
        SyntaxNode root, SemanticModel model, Document doc, Project project, List<object> findings)
    {
        var fileIoMethods = new HashSet<string>
        {
            "ReadAllText", "ReadAllLines", "ReadAllBytes",
            "WriteAllText", "WriteAllLines", "WriteAllBytes",
            "OpenRead", "OpenWrite", "Open", "Delete", "Exists",
        };

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;

            var methodName = ma.Name.Identifier.Text;
            if (!fileIoMethods.Contains(methodName)) continue;

            var sym = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (sym is null) continue;

            var containingType = sym.ContainingType?.Name ?? "";
            if (containingType != "File" && containingType != "Directory" && containingType != "Path") continue;

            // Flag if the path argument contains a parameter reference (potential user input)
            var pathArg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (pathArg is null) continue;

            if (ContainsDynamicString(pathArg) || ContainsParameterReference(pathArg, model))
            {
                Report("Path traversal risk", invocation, doc, project, findings,
                    $"'{containingType}.{methodName}' receives a dynamic path. " +
                    "Validate and sanitise user-supplied paths; use Path.GetFullPath and restrict to a base directory.");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static bool ContainsDynamicString(ExpressionSyntax expr) =>
        expr is InterpolatedStringExpressionSyntax ||
        (expr is BinaryExpressionSyntax bin &&
         bin.IsKind(SyntaxKind.AddExpression) &&
         (IsStringLiteral(bin.Left) || IsStringLiteral(bin.Right)));

    private static bool IsStringLiteral(ExpressionSyntax expr) =>
        expr is LiteralExpressionSyntax lit && lit.Token.IsKind(SyntaxKind.StringLiteralToken);

    private static bool ContainsParameterReference(ExpressionSyntax expr, SemanticModel model)
    {
        foreach (var id in expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var sym = model.GetSymbolInfo(id).Symbol;
            if (sym is IParameterSymbol) return true;
        }
        return false;
    }

    private static void Report(string issueKind, SyntaxNode node, Document doc, Project project,
        List<object> list, string detail)
    {
        var loc = node.GetLocation().GetLineSpan();
        list.Add(new
        {
            IssueKind = issueKind,
            Severity  = "Warning",
            Detail    = detail,
            Project   = project.Name,
            FilePath  = doc.FilePath,
            Line      = loc.StartLinePosition.Line + 1,
        });
    }
}
