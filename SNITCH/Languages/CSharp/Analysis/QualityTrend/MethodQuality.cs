namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// One executable body in a file — a method, constructor, operator, local function or accessor — with
/// the two numbers the trend is built from: its cyclomatic complexity and its length in lines.
/// </summary>
public sealed record MethodQuality(string Name, int Line, int Complexity, int Lines);
