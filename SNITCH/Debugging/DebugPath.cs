namespace Snitch.Debugging;

/// <summary>File-path comparison for a debugger, where the adapter and the solution spell the same file differently.</summary>
internal static class DebugPath
{
    internal static bool Equal(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
