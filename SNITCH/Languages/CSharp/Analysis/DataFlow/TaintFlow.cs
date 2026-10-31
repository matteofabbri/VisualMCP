namespace Snitch.Languages.CSharp.Analysis.DataFlow;

/// <summary>
/// Where taint enters a method: a parameter the framework binds, or an expression that reads
/// untrusted input. The expression is the text the flow started from.
/// </summary>
public sealed record FlowSource(string Kind, string Expression, string File, int Line);

/// <summary>Where taint must not arrive, with the CWE the sink belongs to.</summary>
public sealed record FlowSink(string Kind, string Cwe, string Severity, string Expression, string File, int Line);

/// <summary>One hop between the source and the sink, so a reader can follow the value.</summary>
public sealed record FlowStep(string File, int Line, string What);

/// <summary>A value that reaches a dangerous operation, with the path it took.</summary>
public sealed record TaintFlow(FlowSource Source, FlowSink Sink, IReadOnlyList<FlowStep> Steps, int Hops);

/// <summary>The result of one scan: the flows plus the numbers that say how much was looked at.</summary>
public sealed record TaintScan(
    IReadOnlyList<TaintFlow> Flows, int MethodsAnalyzed, int SourceParameters, int InterproceduralCalls);
