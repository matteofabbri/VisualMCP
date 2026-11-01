namespace Snitch.Debugging;

/// <summary>One place the solution's source writes a member, found by Roslyn for a data breakpoint.</summary>
public sealed record DataBreakpointSite(string File, int Line, string Kind);

/// <summary>
/// A data breakpoint as this product can deliver it: a member whose every write site in the
/// solution's C# source becomes a line breakpoint. The adapter has no data-breakpoint request, so
/// the break happens where the source assigns the member — which is the stop a user is after.
/// </summary>
public sealed record DataBreakpoint(string Member, string Symbol, DataBreakpointSite[] Sites);

/// <summary>The arguments a launch used, kept so <c>debug_restart</c> can repeat them.</summary>
public sealed record LaunchSpec(
    string Program,
    string[]? Args,
    string? WorkingDirectory,
    string? EnvironmentJson,
    bool StopAtEntry);
