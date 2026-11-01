namespace Snitch.Languages.CSharp.Benchmarking;

public interface IRunBenchmarksService
{
    Task<object> RunAsync(string? projectName, string filter, string? extraArgs, bool noBuild, int timeoutSeconds);
}
