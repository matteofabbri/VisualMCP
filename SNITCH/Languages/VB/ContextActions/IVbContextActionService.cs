namespace Snitch.Languages.VB.ContextActions;

/// <summary>
/// The Visual Basic half of <c>context_action</c>: the same contract as the C# service, applied to
/// <c>.vb</c> syntax. The ids it accepts are the C# catalogue entries that have a VB spelling; the ones
/// that are C# syntax with no VB counterpart (raw/verbatim strings, braces, <c>#nullable</c>, patterns,
/// the C# 14 extension block) are answered with a written reason, never silently ignored.
/// </summary>
public interface IVbContextActionService
{
    /// <summary>Applies one action to the Visual Basic document that contains <paramref name="line"/>.</summary>
    /// <param name="action">One of <c>VbContextActionService.Catalogue</c> (e.g. <c>check_param_null</c>).</param>
    /// <param name="filePath">The document, absolute or relative to the server's working directory.</param>
    /// <param name="line">1-based anchor line: the type, the method, the literal or the first wrapped statement.</param>
    /// <param name="toLine">Last line of the wrapped range; defaults to <paramref name="line"/>.</param>
    /// <param name="text">Action-specific argument: parameter name, condition, resource expression, type name.</param>
    /// <param name="dryRun">Preview only unless false.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<object> Apply(
        string action,
        string filePath,
        int line,
        int? toLine = null,
        string? text = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default);
}
