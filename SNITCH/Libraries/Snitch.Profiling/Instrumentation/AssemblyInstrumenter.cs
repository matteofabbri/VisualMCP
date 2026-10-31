using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Instrumentation;

public interface IAssemblyInstrumenter
{
    /// <summary>
    /// Rewrites every method of an assembly so it reports enter/exit to the probe, writes the rewritten
    /// copy and the name map next to it, and copies the probe assembly beside them.
    /// </summary>
    InstrumentationResult Instrument(
        string assemblyPath,
        string? outputPath,
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude);
}

/// <summary>
/// The IL side of instrumented profiling — the counterpart of Visual Studio's Instrumentation tool,
/// done without a build step: every selected method of a compiled assembly is rewritten so it calls a
/// probe on entry and on every exit (including the exception path, via a <c>fault</c> handler), and the
/// rewritten copy is what you run.
///
/// The probe is <c>Snitch.Profiling.Probe.dll</c>, a tiny EventSource; the instrumented copy is placed
/// next to it and the caller runs the copy. The events then carry a method id, and the map file written
/// here turns that id back into the method name — exact call counts and exact inclusive/exclusive times,
/// with no sampling.
///
/// A method whose body cannot be rewritten safely is skipped and named in the notes rather than
/// producing an assembly that fails to run: a <c>jmp</c>, or a <c>ret</c> inside a finally/fault block.
/// </summary>
public sealed class AssemblyInstrumenter : IAssemblyInstrumenter
{
    private const string ProbeAssemblyName = "Snitch.Profiling.Probe";
    private const string ProbeTypeName = "Snitch.Profiling.Probe.Probe";

    private static readonly Version ProbeVersion = new(1, 0, 0, 0);

    public InstrumentationResult Instrument(
        string assemblyPath,
        string? outputPath,
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude)
    {
        var source = Path.GetFullPath(assemblyPath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Assembly not found: {source}", source);

        var output = ResolveOutput(source, outputPath);
        var outputDirectory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(outputDirectory);

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(source)!);

        using var assembly = AssemblyDefinition.ReadAssembly(source, new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = false,
            ReadingMode = ReadingMode.Immediate,
            InMemory = true,
        });

        var module = assembly.MainModule;
        var probeReference = EnsureProbeReference(module);
        var enter = ProbeMethod(module, probeReference, "Enter");
        var exit = ProbeMethod(module, probeReference, "Exit");

        var notes = new List<string>();
        var map = new List<InstrumentationMethodMap>();
        int id = 0, methods = 0, skipped = 0, types = 0;

        foreach (var type in module.GetTypes())
        {
            if (type.Name == "<Module>") continue;
            types++;

            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.IsAbstract || method.IsPInvokeImpl || method.IsRuntime || method.IsInternalCall)
                    continue;

                var fullName = $"{type.FullName.Replace('/', '.')}.{method.Name}";
                if (!Included(fullName, include, exclude)) continue;
                if (!method.Body.Instructions.Any())
                    continue;

                if (Unsupported(method, out var reason))
                {
                    skipped++;
                    if (notes.Count < 25) notes.Add($"{fullName}: skipped ({reason})");
                    continue;
                }

                id++;
                Rewrite(method, id, enter, exit);
                methods++;
                map.Add(new InstrumentationMethodMap(id, fullName, method.MetadataToken.ToInt32()));
            }
        }

        assembly.Write(output);

        var probeSource = typeof(Snitch.Profiling.Probe.Probe).Assembly.Location;
        var probeTarget = Path.Combine(outputDirectory, ProbeAssemblyName + ".dll");
        var copiedProbe = false;
        if (!string.IsNullOrEmpty(probeSource) && File.Exists(probeSource) &&
            !string.Equals(Path.GetFullPath(probeSource), Path.GetFullPath(probeTarget), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(probeSource, probeTarget, overwrite: true);
            copiedProbe = true;
        }

        var mapPath = output + ".map.json";
        new InstrumentationMapFile(Snitch.Profiling.Probe.Probe.ProviderName, source, output, map).Save(mapPath);

        notes.Insert(0,
            copiedProbe
                ? $"Probe copied to {probeTarget}. Run the instrumented copy and keep the probe DLL beside it."
                : $"The probe DLL was already beside the output ({probeTarget}).");
        notes.Add(
            "Collect the exact timings with profile_collect profile=instrumentation while the instrumented copy runs, " +
            "then read them with profile_instrumentation action=analyze.");

        return new InstrumentationResult(
            source,
            output,
            Path.GetFullPath(mapPath),
            Snitch.Profiling.Probe.Probe.ProviderName,
            types,
            methods,
            skipped,
            new FileInfo(output).Length,
            notes);
    }

    /// <summary>The output path: beside the input, named <c>&lt;name&gt;.instrumented.dll</c>, unless the caller named one.</summary>
    private static string ResolveOutput(string source, string? outputPath)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            // A directory (existing, or clearly a folder) gets the generated file name.
            if (Directory.Exists(outputPath) || outputPath.EndsWith(Path.DirectorySeparatorChar) ||
                outputPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                return Path.Combine(Path.GetFullPath(outputPath), Path.GetFileNameWithoutExtension(source) + ".instrumented.dll");
            }

            return Path.GetFullPath(outputPath);
        }

        var directory = Path.GetDirectoryName(source)!;
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + ".instrumented.dll");
    }

    private static AssemblyNameReference EnsureProbeReference(ModuleDefinition module)
    {
        var existing = module.AssemblyReferences.FirstOrDefault(reference =>
            string.Equals(reference.Name, ProbeAssemblyName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var reference = new AssemblyNameReference(ProbeAssemblyName, ProbeVersion);
        module.AssemblyReferences.Add(reference);
        return reference;
    }

    private static MethodReference ProbeMethod(ModuleDefinition module, AssemblyNameReference probe, string name)
    {
        var type = new TypeReference("Snitch.Profiling.Probe", "Probe", module, probe, valueType: false);
        var method = new MethodReference(name, module.TypeSystem.Void, type) { HasThis = false };
        method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        return method;
    }

    /// <summary>
    /// Whether a body can be wrapped in the probe's try/fault safely. The two shapes that cannot are a
    /// <c>jmp</c> (which transfers control out of the method) and a <c>ret</c> that already lives inside
    /// a finally/fault block (leaving it with <c>leave</c> would change unwinding).
    /// </summary>
    private static bool Unsupported(MethodDefinition method, out string reason)
    {
        var body = method.Body;

        if (body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Jmp))
        {
            reason = "the body uses jmp";
            return true;
        }

        var protectedHandlers = body.ExceptionHandlers
            .Where(handler => handler.HandlerType is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault)
            .Where(handler => handler.HandlerStart is not null && handler.HandlerEnd is not null)
            .ToList();

        foreach (var ret in body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret))
        {
            foreach (var handler in protectedHandlers)
            {
                if (ret.Offset >= handler.HandlerStart!.Offset && ret.Offset < handler.HandlerEnd!.Offset)
                {
                    reason = "a ret lives inside a finally/fault block";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Enter at the top, exit before every return and from a <c>fault</c> handler. The <c>ret</c>
    /// instructions are mutated into <c>leave</c> rather than removed, so an exception-handler range
    /// that ends on one keeps pointing at a real instruction.
    /// </summary>
    private static void Rewrite(MethodDefinition method, int id, MethodReference enter, MethodReference exit)
    {
        var body = method.Body;
        body.SimplifyMacros();
        var il = body.GetILProcessor();

        var returnsValue = method.ReturnType.MetadataType != MetadataType.Void;
        VariableDefinition? result = null;
        if (returnsValue)
        {
            result = new VariableDefinition(method.Module.ImportReference(method.ReturnType));
            body.Variables.Add(result);
            body.InitLocals = true;
        }

        var first = body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, id));
        il.InsertBefore(first, il.Create(OpCodes.Call, enter));

        var end = Instruction.Create(OpCodes.Nop);

        foreach (var ret in body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToList())
        {
            if (returnsValue)
                il.InsertBefore(ret, il.Create(OpCodes.Stloc, result!));

            ret.OpCode = OpCodes.Leave;
            ret.Operand = end;
        }

        // The handler goes right after the try, the normal exit after the handler: the layout every
        // compiler emits, and the one the CLR expects a fault block to have.
        var handlerStart = il.Create(OpCodes.Ldc_I4, id);
        il.Append(handlerStart);
        il.Append(il.Create(OpCodes.Call, exit));
        il.Append(il.Create(OpCodes.Endfinally));
        var handlerEnd = il.Create(OpCodes.Nop);
        il.Append(handlerEnd);

        il.Append(end);
        il.Append(il.Create(OpCodes.Ldc_I4, id));
        il.Append(il.Create(OpCodes.Call, exit));
        if (returnsValue)
            il.Append(il.Create(OpCodes.Ldloc, result!));
        il.Append(il.Create(OpCodes.Ret));

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Fault)
        {
            TryStart = first,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = handlerEnd,
        });

        body.OptimizeMacros();
    }

    /// <summary>
    /// A simple wildcard match on the full method name: <c>*</c> spans anything, everything else is a
    /// case-insensitive substring comparison, so <c>MyApp.Services.*</c> and <c>*OrderService*</c> both do
    /// what they read like. No include patterns means "everything".
    /// </summary>
    private static bool Included(string fullName, IReadOnlyList<string>? include, IReadOnlyList<string>? exclude)
    {
        if (exclude is { Count: > 0 } && exclude.Any(pattern => Matches(fullName, pattern)))
            return false;

        if (include is null || include.Count == 0) return true;
        return include.Any(pattern => Matches(fullName, pattern));
    }

    private static bool Matches(string text, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var parts = pattern.Trim().Split('*');
        var position = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;

            var found = text.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;

            if (i == 0 && !pattern.StartsWith('*') && found != 0) return false;
            position = found + part.Length;
        }

        if (!pattern.EndsWith('*') && parts.Length > 0)
            return text.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase);

        return true;
    }
}
