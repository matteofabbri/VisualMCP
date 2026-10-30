using System.Runtime.CompilerServices;

namespace VisualMCP.Tests;

/// <summary>
/// SQLite's native library is extracted to a hashed path on first use; two test classes opening the
/// board database at the same moment race on that file. Initialising the provider once, before any
/// test runs, removes the race.
/// </summary>
internal static class TestBootstrap
{
    [ModuleInitializer]
    internal static void Initialise() => SQLitePCL.Batteries_V2.Init();
}
