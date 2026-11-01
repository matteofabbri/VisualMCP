namespace Snitch.Languages.CSharp.ContextActions;

/// <summary>
/// The ReSharper context actions that are neither IDE-style rewrites (<c>simplify_code</c>) nor whole
/// refactorings: importing a type, generating argument guards, wrapping a statement range and converting
/// a string literal between its regular, verbatim and raw forms.
/// </summary>
public interface IContextActionService
{
    /// <summary>
    /// Applies one action to the document that contains <paramref name="line"/>.
    /// </summary>
    /// <param name="action">One of <c>ContextActionService.Catalogue</c> (e.g. <c>import_type</c>).</param>
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
