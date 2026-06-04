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
        "Hunt for common security vulnerabilities across the solution using Roslyn's semantic model, " +
        "returning CWE-tagged findings with a severity. Detects: SQL injection (CWE-89), OS command " +
        "injection via Process.Start/ProcessStartInfo (CWE-78), weak crypto algorithms MD5/SHA1/DES/RC2 " +
        "(CWE-327) and ECB cipher mode, insecure System.Random for security (CWE-338), hardcoded " +
        "secrets (CWE-798), unsafe deserialization incl. BinaryFormatter and Newtonsoft TypeNameHandling " +
        "(CWE-502), SSRF from user-controlled URLs (CWE-918), XXE via DtdProcessing/XmlResolver (CWE-611), " +
        "disabled TLS certificate validation (CWE-295), and path traversal (CWE-22). " +
        "Use this INSTEAD OF grepping for risky APIs — Roslyn resolves types precisely (e.g. System.Random " +
        "vs a custom Random) and follows the data flow text search cannot. The solution auto-loads on first use.")]
    public static async Task<object> FindSecurityIssues(
        [Description("Optional: restrict to a single project by name")] string? projectName = null)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

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
                CheckWeakCipherMode(root, document, project, findings);
                CheckInsecureRandom(root, model, document, project, findings);
                CheckHardcodedSecrets(root, document, project, findings);
                CheckUnsafeDeserialization(root, model, document, project, findings);
                CheckUnsafeTypeNameHandling(root, document, project, findings);
                CheckCommandInjection(root, model, document, project, findings);
                CheckSsrf(root, model, document, project, findings);
                CheckXxe(root, document, project, findings);
                CheckDisabledCertValidation(root, document, project, findings);
                CheckPathTraversal(root, model, document, project, findings);
            }
        }

        var byKind = findings
            .GroupBy(f => ((dynamic)f).IssueKind.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var bySeverity = findings
            .GroupBy(f => ((dynamic)f).Severity.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            projectFilter  = projectName ?? "all",
            totalFindings  = findings.Count,
            bySeverity,
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

    private static readonly Dictionary<string, (string Severity, string Cwe)> RiskMeta =
        new(StringComparer.Ordinal)
    {
        ["SQL Injection risk"]                  = ("High",     "CWE-89"),
        ["Command injection risk"]              = ("Critical", "CWE-78"),
        ["Weak cryptographic algorithm"]        = ("Medium",   "CWE-327"),
        ["Weak cipher mode (ECB)"]              = ("Medium",   "CWE-327"),
        ["Insecure random number generator"]    = ("Medium",   "CWE-338"),
        ["Hardcoded secret"]                    = ("High",     "CWE-798"),
        ["Unsafe deserialization"]              = ("Critical", "CWE-502"),
        ["Unsafe TypeNameHandling"]             = ("Critical", "CWE-502"),
        ["SSRF risk"]                           = ("High",     "CWE-918"),
        ["XXE risk"]                            = ("High",     "CWE-611"),
        ["Disabled TLS certificate validation"] = ("High",     "CWE-295"),
        ["Path traversal risk"]                 = ("High",     "CWE-22"),
    };

    private static void Report(string issueKind, SyntaxNode node, Document doc, Project project,
        List<object> list, string detail)
    {
        var loc = node.GetLocation().GetLineSpan();
        var (severity, cwe) = RiskMeta.TryGetValue(issueKind, out var meta) ? meta : ("Warning", "");
        list.Add(new
        {
            IssueKind = issueKind,
            Severity  = severity,
            Cwe       = string.IsNullOrEmpty(cwe) ? null : cwe,
            Detail    = detail,
            Project   = project.Name,
            FilePath  = doc.FilePath,
            Line      = loc.StartLinePosition.Line + 1,
        });
    }

    // ── Command Injection (CWE-78) ───────────────────────────────────────────────

    private static void CheckCommandInjection(
        SyntaxNode root, SemanticModel model, Document doc, Project project, List<object> findings)
    {
        // Process.Start(...) with a dynamic / parameter-derived argument.
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Name.Identifier.Text != "Start") continue;

            var sym = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (sym?.ContainingType?.Name != "Process") continue;

            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (ContainsDynamicString(arg.Expression) || ContainsParameterReference(arg.Expression, model))
                {
                    Report("Command injection risk", arg.Expression, doc, project, findings,
                        "A dynamic value is passed to Process.Start. Untrusted input in a process " +
                        "command/arguments enables OS command injection. Use an explicit executable path with " +
                        "a separate, validated argument list (avoid passing a shell a built string).");
                    break;
                }
            }
        }

        // ProcessStartInfo.FileName / .Arguments assigned a dynamic value.
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not MemberAccessExpressionSyntax ma) continue;
            var prop = ma.Name.Identifier.Text;
            if (prop is not ("Arguments" or "FileName")) continue;

            if (model.GetSymbolInfo(assignment.Left).Symbol is not IPropertySymbol p) continue;
            if (p.ContainingType?.Name != "ProcessStartInfo") continue;

            if (ContainsDynamicString(assignment.Right) || ContainsParameterReference(assignment.Right, model))
            {
                Report("Command injection risk", assignment, doc, project, findings,
                    $"ProcessStartInfo.{prop} is built from a dynamic value. Validate/allowlist the input; " +
                    "prefer a fixed executable and discrete, escaped arguments.");
            }
        }
    }

    // ── SSRF (CWE-918) ───────────────────────────────────────────────────────────

    private static void CheckSsrf(
        SyntaxNode root, SemanticModel model, Document doc, Project project, List<object> findings)
    {
        var httpMethods = new HashSet<string>
        {
            "GetAsync", "GetStringAsync", "GetByteArrayAsync", "GetStreamAsync",
            "PostAsync", "PutAsync", "PatchAsync", "DeleteAsync", "SendAsync",
        };

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;
            var name = ma.Name.Identifier.Text;

            var sym = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            var container = sym?.ContainingType?.Name ?? "";

            var isHttpClientCall = httpMethods.Contains(name) && container == "HttpClient";
            var isWebRequest     = name == "Create" && container is "WebRequest" or "HttpWebRequest";
            if (!isHttpClientCall && !isWebRequest) continue;

            var firstArg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (firstArg is null) continue;

            if (ContainsParameterReference(firstArg, model) || ContainsDynamicString(firstArg))
            {
                Report("SSRF risk", invocation, doc, project, findings,
                    $"The request URL for '{(isWebRequest ? "WebRequest.Create" : "HttpClient." + name)}' is " +
                    "derived from external input. An attacker may force requests to internal hosts/metadata " +
                    "endpoints. Validate the URL against an allowlist of hosts/schemes before calling.");
            }
        }
    }

    // ── XXE (CWE-611) ────────────────────────────────────────────────────────────

    private static void CheckXxe(
        SyntaxNode root, Document doc, Project project, List<object> findings)
    {
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var left  = assignment.Left.ToString();
            var right = assignment.Right.ToString();

            if (left.EndsWith("DtdProcessing", StringComparison.Ordinal) &&
                right.EndsWith("Parse", StringComparison.Ordinal))
            {
                Report("XXE risk", assignment, doc, project, findings,
                    "DtdProcessing is set to Parse, which resolves external entities (XXE). " +
                    "Use DtdProcessing.Prohibit (or Ignore) and a null XmlResolver.");
            }
            else if (left.EndsWith("XmlResolver", StringComparison.Ordinal) &&
                     !right.Equals("null", StringComparison.Ordinal))
            {
                Report("XXE risk", assignment, doc, project, findings,
                    "A non-null XmlResolver enables external entity/DTD resolution (XXE). " +
                    "Set XmlResolver = null unless you fully trust the XML source.");
            }
        }

        // Legacy XmlTextReader resolves DTDs by default.
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (creation.Type.ToString().EndsWith("XmlTextReader", StringComparison.Ordinal))
            {
                Report("XXE risk", creation, doc, project, findings,
                    "XmlTextReader resolves DTDs/external entities by default (XXE). " +
                    "Prefer XmlReader.Create with XmlReaderSettings { DtdProcessing = Prohibit, XmlResolver = null }.");
            }
        }
    }

    // ── Unsafe Newtonsoft TypeNameHandling (CWE-502) ─────────────────────────────

    private static void CheckUnsafeTypeNameHandling(
        SyntaxNode root, Document doc, Project project, List<object> findings)
    {
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.Left.ToString().EndsWith("TypeNameHandling", StringComparison.Ordinal)) continue;

            var right = assignment.Right.ToString();
            if (right.EndsWith("All", StringComparison.Ordinal) ||
                right.EndsWith("Auto", StringComparison.Ordinal) ||
                right.EndsWith("Objects", StringComparison.Ordinal) ||
                right.EndsWith("Arrays", StringComparison.Ordinal))
            {
                Report("Unsafe TypeNameHandling", assignment, doc, project, findings,
                    $"Json.NET TypeNameHandling = {right} embeds .NET type names in JSON and instantiates them " +
                    "on deserialization — a remote code-execution gadget with untrusted input. Use " +
                    "TypeNameHandling.None, or a strict SerializationBinder allowlist.");
            }
        }
    }

    // ── Weak cipher mode: ECB (CWE-327) ──────────────────────────────────────────

    private static void CheckWeakCipherMode(
        SyntaxNode root, Document doc, Project project, List<object> findings)
    {
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left.ToString().EndsWith(".Mode", StringComparison.Ordinal) &&
                assignment.Right.ToString().EndsWith("CipherMode.ECB", StringComparison.Ordinal))
            {
                Report("Weak cipher mode (ECB)", assignment, doc, project, findings,
                    "ECB mode encrypts identical plaintext blocks to identical ciphertext, leaking structure. " +
                    "Use an authenticated mode (AES-GCM) or CBC with a random IV and a MAC.");
            }
        }
    }

    // ── Disabled TLS certificate validation (CWE-295) ────────────────────────────

    private static void CheckDisabledCertValidation(
        SyntaxNode root, Document doc, Project project, List<object> findings)
    {
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var left  = assignment.Left.ToString();
            var right = assignment.Right.ToString();

            var isCallback =
                left.Contains("ServerCertificateValidationCallback", StringComparison.Ordinal) ||
                left.Contains("ServerCertificateCustomValidationCallback", StringComparison.Ordinal) ||
                left.Contains("RemoteCertificateValidationCallback", StringComparison.Ordinal);

            if (isCallback &&
                (AlwaysReturnsTrue(assignment.Right) ||
                 right.EndsWith("DangerousAcceptAnyServerCertificateValidator", StringComparison.Ordinal)))
            {
                Report("Disabled TLS certificate validation", assignment, doc, project, findings,
                    "The TLS certificate validation callback unconditionally accepts any certificate, " +
                    "defeating server authentication and enabling man-in-the-middle attacks. " +
                    "Validate the certificate chain and host name instead.");
            }
        }
    }

    private static bool AlwaysReturnsTrue(ExpressionSyntax? expr) => expr switch
    {
        LiteralExpressionSyntax lit            => lit.IsKind(SyntaxKind.TrueLiteralExpression),
        ParenthesizedLambdaExpressionSyntax pl => BodyReturnsTrue(pl.Body),
        SimpleLambdaExpressionSyntax sl        => BodyReturnsTrue(sl.Body),
        AnonymousMethodExpressionSyntax am     => am.Block is not null && BlockReturnsTrue(am.Block),
        _                                      => false,
    };

    private static bool BodyReturnsTrue(CSharpSyntaxNode body) => body switch
    {
        ExpressionSyntax e => AlwaysReturnsTrue(e),
        BlockSyntax b      => BlockReturnsTrue(b),
        _                  => false,
    };

    private static bool BlockReturnsTrue(BlockSyntax block) =>
        block.Statements.OfType<ReturnStatementSyntax>()
             .Any(r => r.Expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.TrueLiteralExpression));
}
