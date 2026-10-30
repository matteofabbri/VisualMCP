using Snitch.Metacode.Decompilation;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers Snitch.Metacode: the translator itself (a singleton — it holds only the frontend list),
/// the ILSpy decompiler that produces metacode, and the Ghidra headless analyzer that produces
/// metacode from a native binary.
/// </summary>
/// <remarks>
/// The decompiler used to be registered by <c>AddSnitch()</c> while its implementation lived in the
/// Snitch assembly. It lives here now, so the registration moved with it: a host that wants
/// decompilation calls <see cref="AddSnitchMetacode"/> next to <see cref="SnitchServiceCollectionExtensions.AddSnitch"/>.
/// </remarks>
public static class SnitchMetacodeServiceCollectionExtensions
{
    public static IServiceCollection AddSnitchMetacode(this IServiceCollection services)
    {
        services.AddSingleton(Snitch.Metacode.MetacodeTranslator.Default);

        // One implementation, two contracts: the text decompiler every existing caller resolves, and
        // the metacode one that lowers the same output into the model.
        services.AddSingleton<DecompileService>();
        services.AddSingleton<IDecompileService>(sp => sp.GetRequiredService<DecompileService>());
        services.AddSingleton<IMetacodeDecompileService>(sp => sp.GetRequiredService<DecompileService>());

        // Ghidra: the locator/installer first (the latest Ghidra release is downloaded into the per-user
        // tools folder when no installation is found), then the headless service that runs it, then the
        // tables where its output is kept. HostSystem decides the launcher name and the execute bits.
        services.AddSingleton<Snitch.Metacode.Decompilation.IGhidraInstaller, Snitch.Metacode.Decompilation.GhidraInstaller>();
        services.AddSingleton<Snitch.Metacode.Decompilation.GhidraOptions>(_ => Snitch.Metacode.Decompilation.GhidraOptions.Default);
        services.AddSingleton<Snitch.Metacode.Decompilation.IGhidraService>(sp =>
            new Snitch.Metacode.Decompilation.GhidraService(
                sp.GetRequiredService<Snitch.Metacode.Decompilation.GhidraOptions>(),
                sp.GetRequiredService<Snitch.Metacode.Decompilation.IGhidraInstaller>()));
        services.AddSingleton<Snitch.Metacode.Decompilation.INativeCodeStore, Snitch.Metacode.Decompilation.NativeCodeStore>();

        // The "locate the package, then inspect or decompile" composite: its decompile half is the
        // service above, so its registration belongs here rather than in AddSnitch().
        services.AddSingleton<Snitch.Metacode.Decompilation.IPackageInspectionService,
                              Snitch.Metacode.Decompilation.PackageInspectionService>();

        // The symbol index — where a member is declared, where and how it is used, who uses it — as a
        // persisted index over .visualMCP/visualmcp.db. It resolves the active workspace and the
        // database path from the services AddSnitch() registers.
        services.AddSingleton<Snitch.Metacode.Symbols.ISymbolIndexService, Snitch.Metacode.Symbols.SymbolIndexService>();

        // The solution's metacode: generated when the solution opens, kept in step with the files that
        // change, and the source of the cyclomatic-complexity report and of the C# code generator.
        services.AddSingleton<Snitch.Metacode.Indexing.IMetacodeIndexService, Snitch.Metacode.Indexing.MetacodeIndexService>();

        // The write side of the model: decompile a binary INTO the store, and take a tree back into the
        // solution (rewrite a declaration, add a member) through the compiler-gated file writer.
        services.AddSingleton<Snitch.Metacode.Coding.IMetacodeCodingService, Snitch.Metacode.Coding.MetacodeCodingService>();

        return services;
    }
}
