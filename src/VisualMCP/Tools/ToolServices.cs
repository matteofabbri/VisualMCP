namespace VisualMCP.Tools;

/// <summary>
/// Resolves a capability a host may or may not have registered, so a tool can answer with a sentence
/// instead of the MCP framework's DI exception ("No service for type 'X' has been registered").
///
/// Every other tool injects its service directly: that is the shorter form and the right one when the
/// registration is unconditional. The capabilities reached through here are the ones a host opts into
/// (the Visual Basic layer is registered by <c>AddSnitchVisualBasic()</c>, not by <c>AddSnitch</c> — see
/// its extension method), and the rule for this surface is that a tool whose capability can be absent
/// must answer with a sentence rather than fail the call.
///
/// Why a <see cref="IServiceProvider"/> parameter and not a nullable service parameter: the MCP SDK
/// decides per parameter whether it is a tool argument or a service. It asks the container
/// (<c>IServiceProviderIsService</c>) and, when the answer is yes, binds it from DI and leaves it out of
/// the JSON schema; when the answer is no the parameter becomes a normal argument and gets a schema of
/// its own (AIFunctionMcpServerTool.CreateAIFunctionFactoryOptions, decompiled from
/// ModelContextProtocol.Core 2.3.0). A parameter typed as a service that is not registered would
/// therefore appear in <c>tools/list</c> as a meaningless object argument. <see cref="IServiceProvider"/>
/// is registered by the container itself (ServiceProvider's constructor calls
/// <c>CallSiteFactory.Add(typeof(IServiceProvider), …)</c>), so it is always a service — and it is what
/// lets the tool find out, at call time, whether the optional capability is present.
/// </summary>
internal static class ToolServices
{
    /// <summary>
    /// The registered <typeparamref name="T"/>, or null when this host never registered it.
    /// </summary>
    public static T? Find<T>(IServiceProvider services) where T : class =>
        services.GetService(typeof(T)) as T;

    /// <summary>
    /// What a tool returns when <c>Find&lt;T&gt;</c> came back null: what is missing, how a host
    /// registers it, and the reminder that the tool is still listed.
    /// </summary>
    /// <param name="capability">What the caller asked for, used in the message ("the Visual Basic style engine").</param>
    /// <param name="registration">The extension method that would register it, quoted in the message.</param>
    public static object Missing<T>(string capability, string registration) where T : class => new
    {
        error = $"{capability} is not registered in this host.",
        hint = $"Start the server with {registration} in ConfigureServices " +
               "(this host registers it in src/VisualMCP/Program.cs). Nothing else is needed: the tool " +
               "is present in tools/list either way, so this message is the only symptom.",
        service = typeof(T).FullName,
    };
}
