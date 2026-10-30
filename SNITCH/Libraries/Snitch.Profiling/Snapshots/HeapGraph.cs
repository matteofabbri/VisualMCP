using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Interfaces;

namespace Snitch.Profiling.Snapshots;

/// <summary>One heap object as the retention walker sees it: no ClrMD type, so the walk is testable.</summary>
public readonly record struct HeapNode(ulong Address, string TypeName, long Bytes);

/// <summary>
/// One outgoing reference from an object. <see cref="Field"/> already names the parent field
/// ("System.Collections.Generic.Dictionary`2._entries", "[element]" for array slots), which is
/// what makes a path readable without carrying the ClrMD graph into the analysis.
/// </summary>
public readonly record struct HeapEdge(ulong To, string? Field);

/// <summary>
/// An entry point into the heap (a GC root or a handle) with the label shown to the user.
/// <see cref="Priority"/> orders the search: statics first (the actionable answer for a leak),
/// then stack locals, then handles, so when one object is reachable from several roots the chain
/// the report shows is the one the caller can actually change.
/// </summary>
public sealed record HeapRoot(ulong Address, string Label, bool IsInterior = false, int Priority = 2);

/// <summary>
/// The minimum the retention walk needs from a heap. The ClrMD implementation lives in
/// <see cref="ClrHeapGraph"/>; a test supplies a small fake and exercises the same walk.
/// </summary>
public interface IHeapGraph
{
    /// <summary>False when the target has no walkable heap (triage dump, no DAC); the caller reports that.</summary>
    bool CanWalkHeap { get; }

    IReadOnlyList<HeapRoot> Roots { get; }

    IEnumerable<HeapNode> Objects { get; }

    HeapNode? Get(ulong address);

    IEnumerable<HeapEdge> References(ulong address);
}

/// <summary>
/// Reads a live process or a dump through ClrMD as a plain object graph. The expensive parts are
/// the two enumerations of <see cref="Objects"/> (one header-only pass and one reference pass), so
/// the caller decides when to pay for them.
/// </summary>
internal sealed class ClrHeapGraph : IHeapGraph
{
    private readonly ClrHeap _heap;

    public ClrHeapGraph(ClrSession session) => _heap = session.Runtime.Heap;

    public bool CanWalkHeap => _heap.CanWalkHeap;

    public IReadOnlyList<HeapRoot> Roots
    {
        get
        {
            var roots = new List<HeapRoot>();
            try
            {
                foreach (var root in _heap.EnumerateRoots())
                {
                    var address = SafeAddress(root);
                    if (address == 0)
                        continue;
                    roots.Add(new HeapRoot(address, Label(root), root.IsInterior, Priority(root)));
                }
            }
            catch
            {
                // A corrupted root list should not lose the rest of the answer.
            }
            return roots;
        }
    }

    public IEnumerable<HeapNode> Objects => EnumerateObjects();

    private IEnumerable<HeapNode> EnumerateObjects()
    {
        var enumerator = _heap.EnumerateObjects().GetEnumerator();
        while (true)
        {
            ClrObject obj;
            try
            {
                if (!enumerator.MoveNext())
                    yield break;
                obj = enumerator.Current;
            }
            catch
            {
                yield break;
            }

            if (!obj.IsValid || obj.IsFree || (obj.Type?.IsFree ?? false))
                continue;

            yield return Node(obj);
        }
    }

    public HeapNode? Get(ulong address)
    {
        try
        {
            var obj = _heap.GetObject(address);
            return obj.IsValid && !obj.IsFree ? Node(obj) : null;
        }
        catch
        {
            return null;
        }
    }

    public IEnumerable<HeapEdge> References(ulong address)
    {
        // Materialised because a try/catch cannot wrap a yield: a corrupt object must cost one
        // node, not the whole walk.
        var edges = new List<HeapEdge>();
        try
        {
            var obj = _heap.GetObject(address);
            if (!obj.IsValid || obj.IsFree)
                return edges;

            foreach (var reference in obj.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
            {
                var target = reference.Object;
                if (!target.IsValid || target.Address == 0)
                    continue;
                edges.Add(new HeapEdge(target.Address, FieldLabel(reference)));
            }
        }
        catch
        {
            // Partial edges are still usable; the walk continues from the other objects.
        }
        return edges;
    }

    private static HeapNode Node(ClrObject obj) => new(obj.Address, SafeTypeName(obj), (long)obj.Size);

    private static string SafeTypeName(ClrObject obj)
    {
        try { return obj.Type?.Name ?? "<unknown>"; }
        catch { return "<unknown>"; }
    }

    private static ulong SafeAddress(ClrRoot root)
    {
        try { return root.Object.Address; }
        catch { return 0; }
    }

    private static string? FieldLabel(ClrReference reference)
    {
        try
        {
            if (reference.IsDependentHandle)
                return "dependent handle";
            if (reference.IsArrayElement)
                return "[element]";

            var field = reference.Field;
            if (field is null)
                return null;

            var owner = field.ContainingType?.Name;
            return string.IsNullOrEmpty(owner) ? field.Name : $"{owner}.{field.Name}";
        }
        catch
        {
            return null;
        }
    }

    private static int Priority(ClrRoot root) => root.RootKind switch
    {
        ClrRootKind.StaticVar or ClrRootKind.ThreadStaticVar => 0,
        ClrRootKind.Stack => 1,
        _ => 2,
    };

    private string Label(ClrRoot root)
    {
        var kind = root.RootKind switch
        {
            ClrRootKind.StaticVar => "static",
            ClrRootKind.ThreadStaticVar => "thread-static",
            ClrRootKind.Stack => "stack",
            ClrRootKind.FinalizerQueue => "finalizer queue",
            ClrRootKind.StrongHandle => "GC handle (strong)",
            ClrRootKind.PinnedHandle => "GC handle (pinned)",
            ClrRootKind.AsyncPinnedHandle => "GC handle (async pinned)",
            ClrRootKind.RefCountedHandle => "GC handle (ref-counted)",
            ClrRootKind.SizedRefHandle => "GC handle (sized-ref)",
            _ => root.RootKind.ToString(),
        };

        if (root.RootKind == ClrRootKind.Stack)
        {
            try
            {
                var frame = ((IClrRoot)root).StackFrame?.FrameName;
                if (!string.IsNullOrWhiteSpace(frame))
                    return $"{kind} ({frame})";
            }
            catch
            {
                // The stack frame of a root is optional information.
            }
        }

        // Naming the field a static root comes from is not possible with ClrMD 4.2 as shipped:
        // on a dump of a CoreCLR process ClrStaticField.GetAddress returns 0 and ReadObject an
        // invalid object, for every type (verified on a self-dump). An address-keyed map built from
        // those values produced a *wrong* field name, so the root is reported by kind only and the
        // first frame of the path names the object the static field holds.
        return kind;
    }
}
