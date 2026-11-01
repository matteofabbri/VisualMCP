using Microsoft.CodeAnalysis;
using Snitch.Debugging;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The type conditions <c>debug_exceptions</c> evaluates against the hierarchy: a base class matches the
/// types derived from it (a framework base included), a <c>!</c> condition excludes the whole branch, and a
/// name the model cannot resolve still matches by name instead of hiding the stop.
/// </summary>
public sealed class DebuggerExceptionTypeTests
{
    private static async Task<Compilation> CompileAsync()
    {
        using var workspace = FakeWorkspace.Create(("Errors.cs", """
            using System;

            namespace Probe;

            public class AppException : Exception
            {
                public AppException(string message) : base(message) { }
            }

            public class ValidationException : AppException
            {
                public ValidationException(string message) : base(message) { }
            }

            public sealed class NotFoundException : ValidationException
            {
                public NotFoundException(string message) : base(message) { }
            }

            public sealed class StorageException : AppException
            {
                public StorageException(string message) : base(message) { }
            }

            public sealed class UnrelatedException : InvalidOperationException
            {
                public UnrelatedException(string message) : base(message) { }
            }
            """));

        var solution = await workspace.EnsureSolutionLoadedAsync();
        Assert.NotNull(solution);

        var compilation = await solution!.Projects.First().GetCompilationAsync();
        Assert.NotNull(compilation);
        return compilation!;
    }

    [Fact]
    public async Task ABaseClassMatchesItsDerivedTypesTransitively()
    {
        var compilation = await CompileAsync();

        Assert.True(ExceptionTypeMatcher.Matches(compilation, "Probe.AppException", "AppException", ["AppException"]));
        Assert.True(ExceptionTypeMatcher.Matches(compilation, "Probe.StorageException", "StorageException", ["AppException"]));
        Assert.True(ExceptionTypeMatcher.Matches(compilation, "Probe.NotFoundException", "NotFoundException", ["AppException"]));
        Assert.False(ExceptionTypeMatcher.Matches(compilation, "Probe.UnrelatedException", "UnrelatedException", ["AppException"]));

        // A condition may also be the base and the thrown type the derived one, given either way round.
        Assert.True(ExceptionTypeMatcher.Matches(compilation, null, "NotFoundException", ["ValidationException"]));
        Assert.False(ExceptionTypeMatcher.Matches(compilation, null, "ValidationException", ["NotFoundException"]));
    }

    [Fact]
    public async Task AFrameworkBaseClassMatchesFrameworkDerivedTypes()
    {
        var compilation = await CompileAsync();

        // The adapter compared the full name exactly, so this was the case that did not stop.
        Assert.True(ExceptionTypeMatcher.Matches(compilation, "System.NullReferenceException",
            "NullReferenceException", ["System.Exception"]));
        Assert.True(ExceptionTypeMatcher.Matches(compilation, "System.InvalidOperationException",
            "InvalidOperationException", ["Exception"]));
        Assert.False(ExceptionTypeMatcher.Matches(compilation, "System.InvalidOperationException",
            "InvalidOperationException", ["System.ArgumentException"]));
    }

    [Fact]
    public async Task AnExclusionRemovesTheWholeBranchAndWinsOverAnInclusion()
    {
        var compilation = await CompileAsync();

        Assert.False(ExceptionTypeMatcher.Matches(compilation, "Probe.NotFoundException", "NotFoundException",
            ["!AppException"]));
        Assert.True(ExceptionTypeMatcher.Matches(compilation, "Probe.UnrelatedException", "UnrelatedException",
            ["!AppException"]));

        // With both an inclusion and an exclusion, the exclusion decides.
        Assert.False(ExceptionTypeMatcher.Matches(compilation, "Probe.ValidationException", "ValidationException",
            ["AppException", "!ValidationException"]));
        Assert.True(ExceptionTypeMatcher.Matches(compilation, "Probe.StorageException", "StorageException",
            ["AppException", "!ValidationException"]));
    }

    [Fact]
    public void AnUnresolvableTypeStillMatchesByName()
    {
        // No compilation at all: the fallback compares the full and the simple name, so a filter is never
        // silently ignored when the solution is not loaded.
        Assert.True(ExceptionTypeMatcher.Matches(null, "Contoso.SomeException", "SomeException",
            ["Contoso.SomeException"]));
        Assert.True(ExceptionTypeMatcher.Matches(null, "Contoso.SomeException", "SomeException", ["SomeException"]));
        Assert.False(ExceptionTypeMatcher.Matches(null, "Contoso.SomeException", "SomeException",
            ["Contoso.OtherException"]));
    }

    [Fact]
    public void AnUnidentifiableStopIsShownRatherThanHidden()
    {
        // The adapter reported no type: there is nothing to judge, so the stop is returned.
        Assert.True(ExceptionTypeMatcher.Matches(null, null, null, ["AppException"]));
        Assert.True(ExceptionTypeMatcher.Matches(null, "  ", null, ["AppException"]));
    }
}
