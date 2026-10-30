using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Rendering;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.Metacode;

[McpServerToolType]
public static class AnalyzeNativeBinaryTool
{
    [McpServerTool(Name = "analyze_native_binary"), Description(
        "Recover a NATIVE binary's functions and print them as canonical pseudocode: Ghidra headless imports the file, " +
        "analyzes it and exports every function's decompiled C; that C is normalized and lowered into the same model as " +
        "metacode_translate, so each function comes back with its prototype, its entry address and its body in the shared " +
        "shape (raw C that does not normalize stays visible as RAW + a MC0008 diagnostic). This is a real Ghidra run — an " +
        "external process, tens of seconds and up to timeoutSeconds — not a snapshot reader: to RE-READ a snapshot you " +
        "already have, pass its .json to metacode_translate (or language='ghidra'), which needs no Ghidra at all. Narrow " +
        "the output with function (one name/address) or maxFunctions on a big image; when Ghidra is missing it is " +
        "installed on demand into the per-user tools folder (or point at an existing copy with ghidraHome, headlessPath " +
        "or GHIDRA_INSTALL_DIR). The run is also PERSISTED in the solution database — native_artifact, native_function " +
        "(Ghidra's C plus the canonical pseudocode and complexity per function) and native_call (the call edges) — " +
        "keyed on the binary's SHA-256; pass save=false to print only. Known " +
        "limits of the C reader: the comma operator, multi-level pointers and calling conventions/comments are not " +
        "modelled. Binary analysis is what you do AFTER the managed path is exhausted: decompile_to_metacode for .NET.")]
    public static async Task<object> AnalyzeNativeBinary(
        IGhidraService ghidra,
        INativeCodeStore native,
        [Description("Path to the native binary (PE/ELF/Mach-O) to analyze.")] string binaryPath,
        [Description("Only this function, by name or entry address (with or without 0x). Omit for all of them.")]
        string? function = null,
        [Description("Maximum functions to include in the pseudocode, 0 = all (default). Use it on a large image.")]
        int maxFunctions = 0,
        [Description("Ghidra installation directory (support/analyzeHeadless lives inside it). Default: GHIDRA_INSTALL_DIR/GHIDRA_HOME, then the installer's tools folder.")]
        string? ghidraHome = null,
        [Description("The analyzeHeadless script itself, when it is not under ghidraHome.")] string? headlessPath = null,
        [Description("JDK to hand to Ghidra through JAVA_HOME_OVERRIDE. Omit to let Ghidra use its own, with one retry on a discovered JDK if it complains.")]
        string? javaHome = null,
        [Description("Wall-clock budget for the whole headless run, in seconds (default 300; the process tree is killed after it).")]
        int timeoutSeconds = 300,
        [Description("Ghidra's own per-file analysis budget, in seconds (default 240).")] int analysisTimeoutSeconds = 240,
        [Description("Keep the temporary Ghidra project after the run (useful to repeat a script run).")] bool keepProject = false,
        [Description("Prefix every function with '// @binary:index', so a function can be found again by its position in the snapshot.")]
        bool showOrigin = false,
        [Description("Store the run in the solution database (native_artifact / native_function / native_call), keyed on the binary's SHA-256. Default true.")]
        bool save = true)
    {
        var options = GhidraOptions.Default with
        {
            GhidraHome = ghidraHome,
            HeadlessPath = headlessPath,
            JavaHome = javaHome,
            TimeoutSeconds = Math.Max(1, timeoutSeconds),
            AnalysisTimeoutSeconds = Math.Max(1, analysisTimeoutSeconds),
            KeepProject = keepProject,
        };

        GhidraAnalysis analysis;
        try
        {
            analysis = await ghidra.AnalyzeAsync(binaryPath, options);
        }
        catch (Exception ex)
        {
            return ToolText.Fail($"analyze_native_binary failed: {ex.GetType().Name}: {ex.Message}");
        }

        var name = Path.GetFileName(binaryPath);

        if (analysis.IsError || analysis.Snapshot is null)
        {
            var failure = new StringBuilder();
            failure.Append(name).Append(" — no snapshot: ").Append(analysis.Error ?? "the run produced no analysis.").Append('\n');
            if (analysis.TimedOut)
                failure.Append("It timed out after ").Append(analysis.Elapsed.TotalSeconds.ToString("0")).Append(" s and the process tree was killed.\n");
            else
                failure.Append("Exit code ").Append(analysis.ExitCode).Append(", elapsed ")
                       .Append(analysis.Elapsed.TotalSeconds.ToString("0.0")).Append(" s.\n");
            if (analysis.LogPath is not null) failure.Append("log: ").Append(analysis.LogPath).Append('\n');
            if (analysis.JavaHomeUsed is not null) failure.Append("java: ").Append(analysis.JavaHomeUsed).Append('\n');
            if (!ghidra.IsAvailable && string.IsNullOrWhiteSpace(headlessPath) && string.IsNullOrWhiteSpace(ghidraHome))
                failure.Append("Ghidra was not found on this machine: install it, set GHIDRA_INSTALL_DIR, or pass ghidraHome/headlessPath.\n");
            return ToolText.Fail(failure.ToString());
        }

        var snapshot = analysis.Snapshot!;
        var functions = snapshot.Functions;
        var total = functions.Count;

        string? storage = null;
        if (save)
        {
            try
            {
                var artifact = native.Save(analysis);
                storage = artifact.Error is not null
                    ? "not stored: " + artifact.Error
                    : artifact.Stored
                        ? $"stored: native_artifact #{artifact.Id} · {artifact.Functions} function(s) in native_function · " +
                          $"call edges in native_call ({artifact.Database})"
                        : $"already stored: native_artifact #{artifact.Id} " +
                          $"(SHA-256 {artifact.Sha256[..Math.Min(12, artifact.Sha256.Length)]})";
            }
            catch (Exception exception)
            {
                // The analysis succeeded; losing the copy must not lose the answer.
                storage = $"not stored: {exception.GetType().Name}: {exception.Message}";
            }
        }

        if (!string.IsNullOrWhiteSpace(function))
        {
            var match = snapshot.FindFunction(function);
            if (match is null)
            {
                var known = string.Join(", ", functions.Take(10).Select(f => f.Name));
                return ToolText.Fail(
                    $"'{function}' is not a function of {name}. " +
                    (total == 0 ? "The snapshot has no functions." : $"First {Math.Min(10, total)} of {total}: {known}") +
                    (total > 10 ? ", …" : "."));
            }

            functions = new[] { match };
        }

        if (maxFunctions > 0 && functions.Count > maxFunctions)
            functions = functions.Take(maxFunctions).ToArray();

        var document = new GhidraFrontend().Translate(snapshot with { Functions = functions });
        var rendered = new PseudocodeRenderer(PseudocodeOptions.Default with { ShowOrigin = showOrigin }).Render(document);

        var header = new StringBuilder();
        header.Append(name)
              .Append(" · ").Append(functions.Count).Append(" of ").Append(total).Append(" function(s)")
              .Append(" · ").Append(analysis.Elapsed.TotalSeconds.ToString("0.0")).Append(" s")
              .Append(" · exit ").Append(analysis.ExitCode)
              .Append(" · ").Append(snapshot.Image).Append(" (").Append(snapshot.Language).Append(')');
        if (!string.IsNullOrWhiteSpace(snapshot.Entry)) header.Append(" entry ").Append(snapshot.Entry);
        if (!string.IsNullOrWhiteSpace(snapshot.ImageBase)) header.Append(" base ").Append(snapshot.ImageBase);

        var text = new StringBuilder();
        text.Append(header).Append('\n');
        if (analysis.OutputJsonPath is not null) text.Append("snapshot: ").Append(analysis.OutputJsonPath).Append('\n');
        if (analysis.LogPath is not null) text.Append("log: ").Append(analysis.LogPath).Append('\n');
        if (analysis.JavaHomeUsed is not null) text.Append("java: ").Append(analysis.JavaHomeUsed).Append('\n');
        if (storage is not null) text.Append("store: ").Append(storage).Append('\n');
        text.Append('\n').Append(rendered.Length == 0
            ? "// no pseudocode: no recovered function had a body the C reader could place."
            : rendered);

        var diagnostics = MetacodeToolHelpers.DiagnosticsBlock(document);
        if (diagnostics.Length > 0) text.Append('\n').Append(diagnostics);

        if (maxFunctions > 0 && total > functions.Count)
            text.Append("// ").Append(total - functions.Count)
                .Append(" more function(s) are in the snapshot: pass function= to print a single one, or raise maxFunctions.\n");

        return ToolText.Ok(text.ToString());
    }
}
