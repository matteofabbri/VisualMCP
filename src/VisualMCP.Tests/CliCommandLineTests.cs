using VisualMCP.Cli;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// --cli: the standalone command line is dispatched before the MCP transport, so what is asserted here
/// is the dispatch, the usage contract and the exit codes — the three commands themselves drive the same
/// engines the MCP tools do, asserted elsewhere.
/// </summary>
public sealed class CliCommandLineTests
{
    [Fact]
    public void A_server_invocation_is_not_taken_by_the_command_line()
    {
        Assert.False(CliCommandLine.TryRun([], out _));
        Assert.False(CliCommandLine.TryRun(["serve"], out _));
        Assert.False(CliCommandLine.TryRun(["--hook", "pre-tool-use"], out _));
    }

    [Fact]
    public void Help_prints_the_usage_and_passes()
    {
        Assert.True(CliCommandLine.TryRun(["--cli", "--help"], out var exitCode));
        Assert.Equal(0, exitCode);

        Assert.True(CliCommandLine.TryRun(["--cli"], out var bare));
        Assert.Equal(2, bare);
    }

    [Fact]
    public void An_unknown_command_is_a_usage_error()
    {
        Assert.True(CliCommandLine.TryRun(["--cli", "lint"], out var exitCode));
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void An_option_without_its_value_is_a_usage_error()
    {
        Assert.True(CliCommandLine.TryRun(["--cli", "inspect", "--project"], out var exitCode));
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void A_bare_argument_is_a_usage_error()
    {
        Assert.True(CliCommandLine.TryRun(["--cli", "inspect", "solution.sln"], out var exitCode));
        Assert.Equal(2, exitCode);
    }
}
