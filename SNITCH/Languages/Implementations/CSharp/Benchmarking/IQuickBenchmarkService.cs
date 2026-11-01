namespace Snitch.Languages.CSharp.Benchmarking;

/// <summary>
/// Ad-hoc benchmarking: generates a temporary BenchmarkDotNet project around a
/// code snippet (optionally referencing a solution project) and runs it — so you
/// can measure any code without writing a [Benchmark]-annotated class yourself.
/// </summary>
public interface IQuickBenchmarkService
{
    Task<object> RunAsync(string code, string? setupCode, string[]? usings, string? referenceProject, string? targetFramework, bool quick, int timeoutSeconds);
}
