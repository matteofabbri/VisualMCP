namespace Snitch.Languages.CSharp.Analysis.DataFlow;

/// <summary>What a sink is called in a finding, and the weakness it enables.</summary>
internal sealed record SinkKind(string Kind, string Cwe, string Severity);

/// <summary>
/// The three tables the taint engine reads: where untrusted data comes from, where it must not
/// arrive, and which calls constrain a value before it is passed on. They are deliberately data — a
/// project can extend them without touching the walk — and deliberately explicit, because a taint
/// report is only as honest as the list of sources it admits to knowing.
/// </summary>
internal static class TaintCatalog
{
    /// <summary>Framework parameters whose value the caller controls.</summary>
    public static readonly string[] SourceParameterAttributes =
    [
        "FromQuery", "FromRoute", "FromBody", "FromForm", "FromHeader",
    ];

    /// <summary>Parameter types that *are* the request (a controller, a minimal-API handler).</summary>
    public static readonly string[] SourceParameterTypes =
    [
        "HttpRequest", "HttpRequestMessage", "IFormFile", "IFormFileCollection", "IFormCollection", "HttpContext",
    ];

    /// <summary>Properties whose value is request data: (containing type, member, source kind).</summary>
    public static readonly (string Type, string Member, string Kind)[] SourceMembers =
    [
        ("HttpRequest", "Query", "request query string"),
        ("HttpRequest", "Form", "request form"),
        ("HttpRequest", "Headers", "request header"),
        ("HttpRequest", "Cookies", "request cookie"),
        ("HttpRequest", "Body", "request body"),
        ("HttpRequest", "RouteValues", "route value"),
        ("HttpRequest", "QueryString", "request query string"),
        ("HttpRequest", "Path", "request path"),
        ("HttpContext", "Request", "request"),
        ("ControllerBase", "Request", "request"),
        ("HttpRequestMessage", "Content", "request body"),
    ];

    /// <summary>Methods that read untrusted input: (containing type, method, source kind).</summary>
    public static readonly (string Type, string Method, string Kind)[] SourceMethods =
    [
        ("Console", "ReadLine", "console input"),
        ("Environment", "GetEnvironmentVariable", "environment variable"),
    ];

    /// <summary>
    /// The dangerous calls: (containing type, method, sink, guarded argument positions). A position of
    /// -1 means "every argument"; an empty array means the call itself is the sink.
    /// </summary>
    public static readonly (string Type, string Method, SinkKind Sink, int[] Arguments)[] Sinks =
    [
        ("RelationalDatabaseFacadeExtensions", "ExecuteSqlRaw", Sql(), [0]),
        ("RelationalDatabaseFacadeExtensions", "ExecuteSqlCommand", Sql(), [0]),
        ("RelationalQueryableExtensions", "FromSqlRaw", Sql(), [1]),
        ("EntityFrameworkQueryableExtensions", "FromSqlRaw", Sql(), [1]),
        ("SqlMapper", "Query", Sql(), [0]),
        ("SqlMapper", "QueryAsync", Sql(), [0]),
        ("SqlMapper", "QueryFirst", Sql(), [0]),
        ("SqlMapper", "Execute", Sql(), [0]),
        ("SqlMapper", "ExecuteAsync", Sql(), [0]),
        ("Process", "Start", Command(), [-1]),
        ("File", "ReadAllText", Traversal(), [0]),
        ("File", "ReadAllLines", Traversal(), [0]),
        ("File", "ReadAllBytes", Traversal(), [0]),
        ("File", "WriteAllText", Traversal(), [0]),
        ("File", "WriteAllLines", Traversal(), [0]),
        ("File", "WriteAllBytes", Traversal(), [0]),
        ("File", "AppendAllText", Traversal(), [0]),
        ("File", "Delete", Traversal(), [0]),
        ("File", "Open", Traversal(), [0]),
        ("File", "OpenRead", Traversal(), [0]),
        ("File", "OpenWrite", Traversal(), [0]),
        ("File", "Exists", Traversal(), [0]),
        ("Directory", "Delete", Traversal(), [0]),
        ("Directory", "GetFiles", Traversal(), [0]),
        ("Directory", "GetDirectories", Traversal(), [0]),
        ("Path", "Combine", Traversal(), [-1]),
        ("ControllerBase", "Redirect", OpenRedirect(), [0]),
        ("ControllerBase", "RedirectPermanent", OpenRedirect(), [0]),
        ("HttpResponse", "Redirect", OpenRedirect(), [0]),
        ("HttpResponse", "Write", Xss(), [0]),
        ("HttpResponse", "WriteAsync", Xss(), [0]),
        ("IHtmlHelper", "Raw", Xss(), [0]),
        ("HtmlHelper", "Raw", Xss(), [0]),
        ("HttpClient", "GetAsync", Ssrf(), [0]),
        ("HttpClient", "GetStringAsync", Ssrf(), [0]),
        ("HttpClient", "GetStreamAsync", Ssrf(), [0]),
        ("HttpClient", "GetByteArrayAsync", Ssrf(), [0]),
        ("HttpClient", "PostAsync", Ssrf(), [0]),
        ("HttpClient", "PutAsync", Ssrf(), [0]),
        ("HttpClient", "PatchAsync", Ssrf(), [0]),
        ("HttpClient", "DeleteAsync", Ssrf(), [0]),
        ("HttpClient", "SendAsync", Ssrf(), [1]),
        ("WebRequest", "Create", Ssrf(), [0]),
        ("HttpWebRequest", "Create", Ssrf(), [0]),
    ];

    /// <summary>Assignments whose right side must not be request data: (containing type, member).</summary>
    public static readonly (string Type, string Member, SinkKind Sink)[] SinkMembers =
    [
        ("DbCommand", "CommandText", Sql()),
        ("SqlCommand", "CommandText", Sql()),
        ("ProcessStartInfo", "Arguments", Command()),
        ("ProcessStartInfo", "FileName", Command()),
    ];

    /// <summary>Constructors that are sinks: (type, guarded argument positions).</summary>
    public static readonly (string Type, SinkKind Sink, int[] Arguments)[] SinkConstructors =
    [
        ("SqlCommand", Sql(), [-1]),
        ("FileStream", Traversal(), [0]),
        ("StreamReader", Traversal(), [0]),
        ("StreamWriter", Traversal(), [0]),
    ];

    /// <summary>
    /// Calls that constrain a value to something a sink cannot subvert: a parsed number, a GUID, a
    /// file *name* (which carries no separators), an encoded string. A regular-expression match is
    /// deliberately absent: it leaves the original text intact, so it validates rather than sanitizes.
    /// </summary>
    public static readonly (string Type, string Method)[] Sanitizers =
    [
        ("Int32", "Parse"), ("Int32", "TryParse"), ("Int64", "Parse"), ("Int64", "TryParse"),
        ("Double", "Parse"), ("Double", "TryParse"), ("Decimal", "Parse"), ("Decimal", "TryParse"),
        ("Boolean", "Parse"), ("Boolean", "TryParse"), ("Byte", "Parse"), ("Byte", "TryParse"),
        ("Convert", "ToInt32"), ("Convert", "ToInt64"), ("Convert", "ToBoolean"), ("Convert", "ToDecimal"),
        ("Guid", "Parse"), ("Guid", "TryParse"),
        ("Path", "GetFileName"), ("Path", "GetFileNameWithoutExtension"),
        ("Uri", "EscapeDataString"), ("WebUtility", "UrlEncode"), ("WebUtility", "HtmlEncode"),
        ("HtmlEncoder", "Encode"), ("HttpUtility", "UrlEncode"), ("HttpUtility", "HtmlEncode"),
    ];

    public static SinkKind Sql() => new("SQL injection", "CWE-89", "high");

    public static SinkKind Command() => new("Command injection", "CWE-78", "critical");

    public static SinkKind Traversal() => new("Path traversal", "CWE-22", "high");

    public static SinkKind OpenRedirect() => new("Open redirect", "CWE-601", "medium");

    public static SinkKind Xss() => new("Cross-site scripting", "CWE-79", "high");

    public static SinkKind Ssrf() => new("Server-side request forgery", "CWE-918", "high");
}
