using System.Diagnostics;
using Snitch.Execution;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// An in-process MSBuild evaluation (the Roslyn workspace) leaves MSBuildSDKsPath,
/// MSBuildExtensionsPath and MSBUILD_EXE_PATH in the server process's environment for its whole
/// lifetime. Children must not inherit them: a dotnet build whose CLI resolves a different SDK
/// via global.json would import the pinned SDK's targets and fail with MSB4237.
/// </summary>
public sealed class ProcessRunnerEnvTests
{
    private const string FakeSdk = @"C:\vmcp-fake-sdk\10.0.999";

    [Fact]
    public void RemoveInheritedMsBuildEnv_StripsThePinnedSdkVariables()
    {
        var psi = new ProcessStartInfo("dotnet", "--version");
        psi.Environment["MSBuildSDKsPath"] = FakeSdk + @"\Sdks";
        psi.Environment["MSBuildExtensionsPath"] = FakeSdk;
        psi.Environment["MSBuildToolsPath"] = FakeSdk;
        psi.Environment["MSBUILD_EXE_PATH"] = FakeSdk + @"\MSBuild.dll";

        ProcessRunner.RemoveInheritedMsBuildEnv(psi);

        Assert.False(psi.Environment.ContainsKey("MSBuildSDKsPath"));
        Assert.False(psi.Environment.ContainsKey("MSBuildExtensionsPath"));
        Assert.False(psi.Environment.ContainsKey("MSBuildToolsPath"));
        Assert.False(psi.Environment.ContainsKey("MSBUILD_EXE_PATH"));
    }

    [Fact]
    public async Task RunAsync_ChildrenDoNotSeeTheInheritedMsBuildEnvironment()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Simulate what MSBuildWorkspace leaves behind, then check what the child actually gets.
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", FakeSdk + @"\Sdks");
        try
        {
            var (_, timedOut, stdout, stderr, _) =
                await ProcessRunner.RunAsync("cmd", "/c set MSBuildSDKsPath", Environment.CurrentDirectory, 60);

            Assert.False(timedOut);
            Assert.DoesNotContain(FakeSdk, stdout + stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSBuildSDKsPath", null);
        }
    }
}
