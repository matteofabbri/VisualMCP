using System.Collections.Concurrent;
using Snitch.Metacode;
using Snitch.Metacode.Analysis;
using Snitch.Profiling.Model;

namespace VisualMCP.Profiling;

/// <summary>
/// The join between a profiler measurement and the code: it describes a source file with the solution's
/// <b>metacode AST</b> — the language-neutral model the rest of the product already reads, writes and
/// indexes — so a hot line becomes the construct that produced it.
///
/// It translates the file on demand (the same frontends the solution index uses) and caches the answer
/// on the file's timestamp, so the code that was profiled is the code that is read, even when the
/// workspace snapshot predates the edit. The walk is intentionally the model's shape, not one
/// language's: a loop is a <c>MetacodeWhile</c>/<c>MetacodeForEach</c>/<c>MetacodeFor</c>, an
/// allocation is a <c>MetacodeObjectCreation</c>, and a materialiser is an invocation of a known LINQ
/// name — which is also why the answer would work for a Visual Basic or Razor file without changing
/// this class.
/// </summary>
public sealed class MetacodeCodeStructureIndex : ICodeStructureIndex
{
    private const long MaxFileBytes = 1_000_000;

    private static readonly HashSet<string> Materialisers = new(StringComparer.Ordinal)
    {
        "ToList", "ToArray", "ToDictionary", "ToLookup", "ToHashSet", "ToImmutableList", "ToImmutableArray",
        "Count", "LongCount", "Sum", "Average", "Min", "Max", "Aggregate",
        "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault", "ElementAt",
        "Any", "All",
    };

    private static readonly HashSet<string> Deferred = new(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "OfType", "Cast", "OrderBy", "OrderByDescending", "ThenBy",
        "ThenByDescending", "GroupBy", "Join", "GroupJoin", "Distinct", "Skip", "Take", "SkipWhile",
        "TakeWhile", "Concat", "Union", "Intersect", "Except", "Reverse", "AsEnumerable", "AsQueryable",
    };

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CodeFileStructure Describe(string file)
    {
        string full;
        try
        {
            full = Path.GetFullPath(file);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new CodeFileStructure(file, [], exception.Message);
        }

        if (!File.Exists(full))
            return new CodeFileStructure(full, [], "The file is not on disk (a build-output path, or the file moved).");

        long mtime;
        try
        {
            var info = new FileInfo(full);
            if (info.Length > MaxFileBytes)
                return new CodeFileStructure(full, [], "The file is larger than the code model reads.");
            mtime = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CodeFileStructure(full, [], exception.Message);
        }

        if (_cache.TryGetValue(full, out var cached) && cached.Mtime == mtime)
            return cached.Structure;

        var structure = Build(full);
        _cache[full] = new CacheEntry(mtime, structure);
        return structure;
    }

    private static CodeFileStructure Build(string file)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is not (".cs" or ".vb" or ".cshtml" or ".razor" or ".js" or ".mjs" or ".cjs"))
        {
            return new CodeFileStructure(
                file, [], "No metacode frontend reads this extension (C#, Visual Basic, JavaScript and Razor do).");
        }

        try
        {
            var lines = File.ReadAllLines(file);
            var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromFile(file));
            var declarations = new List<CodeDeclarationInfo>();
            var walker = new Walker(lines);

            foreach (var module in document.Modules)
                Collect(module.Declarations, module.Name, lines, walker, declarations);

            return new CodeFileStructure(file, declarations);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CodeFileStructure(file, [], exception.Message);
        }
    }

    private static void Collect(
        IReadOnlyList<MetacodeDeclaration> declarations,
        string container,
        string[] lines,
        Walker walker,
        List<CodeDeclarationInfo> result)
    {
        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case MetacodeTypeDeclaration type:
                    Collect(type.Members, type.Name, lines, walker, result);
                    break;

                case MetacodeFunction function:
                    result.Add(walker.DescribeFunction(function, container));
                    break;

                case MetacodeProperty property:
                    result.Add(walker.DescribeProperty(property, container));
                    break;
            }
        }
    }

    private sealed record CacheEntry(long Mtime, CodeFileStructure Structure);

    /// <summary>
    /// Walks one declaration's body and records the loops (with their nesting depth) and the constructs
    /// a run-time cost can be attributed to. Local functions and lambdas are their own unit — the same
    /// boundary the complexity analysis uses, so the numbers on a declaration and the constructs inside
    /// it describe the same code.
    /// </summary>
    private sealed class Walker(string[] lines)
    {
        public CodeDeclarationInfo DescribeFunction(MetacodeFunction function, string container)
        {
            var metrics = MetacodeComplexityAnalyzer.Analyze(function);
            var origin = function.Origin;
            var loops = new List<CodeLoopInfo>();
            var constructs = new List<CodeConstructInfo>();

            if (function.Body is not null)
                WalkBlock(function.Body, 0, null, loops, constructs);

            var name = function.Kind == MetacodeFunctionKind.Constructor && function.Name is ".ctor" or "constructor"
                ? container
                : function.Name;

            return new CodeDeclarationInfo(
                name,
                function.Kind.ToString().ToLowerInvariant(),
                origin.Document ?? string.Empty,
                origin.StartLine,
                EndLine(origin),
                metrics.Cyclomatic,
                metrics.Cognitive,
                metrics.MaxNesting,
                metrics.Statements,
                loops,
                constructs);
        }

        public CodeDeclarationInfo DescribeProperty(MetacodeProperty property, string container)
        {
            var metrics = MetacodeComplexityAnalyzer.Analyze(property);
            var loops = new List<CodeLoopInfo>();
            var constructs = new List<CodeConstructInfo>();

            foreach (var accessor in new[] { property.Getter, property.Setter })
            {
                if (accessor?.Body is not null)
                    WalkBlock(accessor.Body, 0, null, loops, constructs);
            }

            var origin = property.Origin;
            return new CodeDeclarationInfo(
                property.Name,
                "property",
                origin.Document ?? string.Empty,
                origin.StartLine,
                EndLine(origin),
                metrics.Cyclomatic,
                metrics.Cognitive,
                metrics.MaxNesting,
                metrics.Statements,
                loops,
                constructs);
        }

        private void WalkBlock(
            MetacodeBlock block,
            int depth,
            string? loopHeader,
            List<CodeLoopInfo> loops,
            List<CodeConstructInfo> constructs)
        {
            foreach (var statement in block.Statements)
                WalkStatement(statement, depth, loopHeader, loops, constructs);
        }

        private void WalkStatement(
            MetacodeStatement statement,
            int depth,
            string? loopHeader,
            List<CodeLoopInfo> loops,
            List<CodeConstructInfo> constructs)
        {
            switch (statement)
            {
                case MetacodeBlock block:
                    WalkBlock(block, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeWhile @while:
                    AddLoop("while", @while.Origin, @while.Body, depth, loopHeader, loops, constructs);
                    WalkExpression(@while.Condition, depth + 1, loopHeader, loops, constructs);
                    return;

                case MetacodeDoWhile doWhile:
                    AddLoop("do", doWhile.Origin, doWhile.Body, depth, loopHeader, loops, constructs);
                    WalkExpression(doWhile.Condition, depth + 1, loopHeader, loops, constructs);
                    return;

                case MetacodeForEach forEach:
                    AddLoop("foreach", forEach.Origin, forEach.Body, depth, loopHeader, loops, constructs,
                        forEach.VariableName);
                    WalkExpression(forEach.Iterable, depth + 1, loopHeader, loops, constructs);
                    return;

                case MetacodeFor @for:
                    AddLoop("for", @for.Origin, @for.Body, depth, loopHeader, loops, constructs);
                    WalkExpression(@for.Condition, depth + 1, loopHeader, loops, constructs);
                    return;

                case MetacodeIf @if:
                    WalkExpression(@if.Condition, depth, loopHeader, loops, constructs);
                    WalkBlock(@if.Then, depth, loopHeader, loops, constructs);
                    if (@if.Else is not null) WalkBlock(@if.Else, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeSwitch @switch:
                    WalkExpression(@switch.Subject, depth, loopHeader, loops, constructs);
                    foreach (var @case in @switch.Cases)
                        WalkBlock(@case.Body, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeTry @try:
                    WalkBlock(@try.Body, depth, loopHeader, loops, constructs);
                    foreach (var @catch in @try.Catches)
                    {
                        constructs.Add(new CodeConstructInfo(
                            "catch", @catch.ExceptionType?.Render() ?? "catch",
                            @catch.Origin.StartLine, EndLine(@catch.Origin), depth, loopHeader));
                        WalkBlock(@catch.Body, depth, loopHeader, loops, constructs);
                    }
                    if (@try.Else is not null) WalkBlock(@try.Else, depth, loopHeader, loops, constructs);
                    if (@try.Finally is not null) WalkBlock(@try.Finally, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeUsing @using:
                    WalkExpression(@using.Resource, depth, loopHeader, loops, constructs);
                    WalkBlock(@using.Body, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeLock @lock:
                    constructs.Add(new CodeConstructInfo(
                        "lock",
                        $"lock ({Describe(@lock.Target)})",
                        @lock.Origin.StartLine, EndLine(@lock.Origin), depth, loopHeader));
                    WalkExpression(@lock.Target, depth, loopHeader, loops, constructs);
                    WalkBlock(@lock.Body, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeLocalFunction:
                    // Its own unit: analysed as a declaration when it is one, never folded into the host.
                    return;

                case MetacodeAssignment assignment:
                    WalkExpression(assignment.Target, depth, loopHeader, loops, constructs);
                    WalkExpression(assignment.Value, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeLocalVariable variable:
                    WalkExpression(variable.Initializer, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeReturn @return:
                    WalkExpression(@return.Value, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeThrow @throw:
                    constructs.Add(new CodeConstructInfo(
                        "throw", Describe(@throw.Exception),
                        @throw.Origin.StartLine, EndLine(@throw.Origin), depth, loopHeader));
                    WalkExpression(@throw.Exception, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeExpressionStatement expressionStatement:
                    WalkExpression(expressionStatement.Expression, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeYield @yield:
                    WalkExpression(@yield.Value, depth, loopHeader, loops, constructs);
                    return;
            }
        }

        private void AddLoop(
            string kind,
            MetacodeOrigin origin,
            MetacodeBlock body,
            int depth,
            string? outerHeader,
            List<CodeLoopInfo> loops,
            List<CodeConstructInfo> constructs,
            string? variable = null)
        {
            var header = HeaderAt(origin.StartLine) ?? kind;
            var inner = depth + 1;

            loops.Add(new CodeLoopInfo(kind, origin.StartLine, EndLine(origin), inner, header));
            constructs.Add(new CodeConstructInfo(
                "loop",
                variable is null ? $"{kind} {header}" : $"{kind} over {variable}",
                origin.StartLine, EndLine(origin), inner, header));

            WalkBlock(body, inner, header, loops, constructs);
        }

        private void WalkExpression(
            MetacodeExpression? expression,
            int depth,
            string? loopHeader,
            List<CodeLoopInfo> loops,
            List<CodeConstructInfo> constructs)
        {
            if (expression is null) return;

            switch (expression)
            {
                case MetacodeInvocation invocation:
                    RecordInvocation(invocation, depth, loopHeader, constructs);
                    WalkExpression(invocation.Callee, depth, loopHeader, loops, constructs);
                    foreach (var argument in invocation.ArgumentList)
                        WalkExpression(argument.Value, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeMemberAccess access:
                    if (access.Member == "Result")
                    {
                        constructs.Add(new CodeConstructInfo(
                            "sync-over-async", $"{Describe(access.Target)}.Result",
                            access.Origin.StartLine, EndLine(access.Origin), depth, loopHeader));
                    }

                    WalkExpression(access.Target, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeObjectCreation creation:
                    constructs.Add(new CodeConstructInfo(
                        "allocation", "new " + creation.Type.Render(),
                        creation.Origin.StartLine, EndLine(creation.Origin), depth, loopHeader));
                    foreach (var argument in creation.Arguments ?? [])
                        WalkExpression(argument.Value, depth, loopHeader, loops, constructs);
                    foreach (var element in creation.CollectionElements ?? [])
                        WalkExpression(element, depth, loopHeader, loops, constructs);
                    foreach (var initializer in creation.MemberInitializers ?? [])
                        WalkExpression(initializer.Value, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeNewArray array:
                    constructs.Add(new CodeConstructInfo(
                        "allocation", "new " + (array.ElementType?.Render() ?? "array") + "[]",
                        array.Origin.StartLine, EndLine(array.Origin), depth, loopHeader));
                    foreach (var size in array.Sizes ?? [])
                        WalkExpression(size, depth, loopHeader, loops, constructs);
                    foreach (var element in array.Elements ?? [])
                        WalkExpression(element, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeCollection collection:
                    constructs.Add(new CodeConstructInfo(
                        "allocation", $"{collection.Kind.ToString().ToLowerInvariant()} literal",
                        collection.Origin.StartLine, EndLine(collection.Origin), depth, loopHeader));
                    foreach (var item in collection.Items)
                        WalkExpression(item, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeDictionary dictionary:
                    constructs.Add(new CodeConstructInfo(
                        "allocation", "dictionary literal",
                        dictionary.Origin.StartLine, EndLine(dictionary.Origin), depth, loopHeader));
                    foreach (var entry in dictionary.Entries)
                    {
                        WalkExpression(entry.Key, depth, loopHeader, loops, constructs);
                        WalkExpression(entry.Value, depth, loopHeader, loops, constructs);
                    }

                    return;

                case MetacodeAnonymousObject anonymous:
                    constructs.Add(new CodeConstructInfo(
                        "allocation", "anonymous object",
                        anonymous.Origin.StartLine, EndLine(anonymous.Origin), depth, loopHeader));
                    foreach (var member in anonymous.Members)
                        WalkExpression(member.Value, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeBinary binary:
                    if (binary.Operator == MetacodeBinaryOperator.Add && LooksLikeString(binary))
                    {
                        constructs.Add(new CodeConstructInfo(
                            "string-concat", Describe(binary),
                            binary.Origin.StartLine, EndLine(binary.Origin), depth, loopHeader));
                    }

                    WalkExpression(binary.Left, depth, loopHeader, loops, constructs);
                    WalkExpression(binary.Right, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeInterpolatedString interpolated:
                    constructs.Add(new CodeConstructInfo(
                        "interpolation", Describe(interpolated),
                        interpolated.Origin.StartLine, EndLine(interpolated.Origin), depth, loopHeader));
                    foreach (var part in interpolated.Parts)
                        WalkExpression(part.Expression, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeAwait awaitExpression:
                    constructs.Add(new CodeConstructInfo(
                        "await", Describe(awaitExpression),
                        awaitExpression.Origin.StartLine, EndLine(awaitExpression.Origin), depth, loopHeader));
                    WalkExpression(awaitExpression.Operand, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeComprehension comprehension:
                    constructs.Add(new CodeConstructInfo(
                        "loop", "comprehension",
                        comprehension.Origin.StartLine, EndLine(comprehension.Origin), depth + 1, loopHeader));
                    loops.Add(new CodeLoopInfo(
                        "comprehension", comprehension.Origin.StartLine, EndLine(comprehension.Origin),
                        depth + 1, "comprehension"));
                    foreach (var clause in comprehension.Clauses)
                        WalkExpression(clause.Expression, depth + 1, loopHeader, loops, constructs);
                    WalkExpression(comprehension.Element, depth + 1, loopHeader, loops, constructs);
                    WalkExpression(comprehension.Value, depth + 1, loopHeader, loops, constructs);
                    return;

                case MetacodeLambda lambda:
                    if (lambda.Expression is not null)
                        WalkExpression(lambda.Expression, depth, loopHeader, loops, constructs);
                    if (lambda.Body is not null)
                        WalkBlock(lambda.Body, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeConditional conditional:
                    WalkExpression(conditional.Condition, depth, loopHeader, loops, constructs);
                    WalkExpression(conditional.WhenTrue, depth, loopHeader, loops, constructs);
                    WalkExpression(conditional.WhenFalse, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeCast cast:
                    WalkExpression(cast.Operand, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeTypeCheck check:
                    WalkExpression(check.Operand, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeUnary unary:
                    WalkExpression(unary.Operand, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeIndex index:
                    WalkExpression(index.Target, depth, loopHeader, loops, constructs);
                    foreach (var subscript in index.Indices)
                        WalkExpression(subscript, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeAssignmentExpression assignment:
                    WalkExpression(assignment.Target, depth, loopHeader, loops, constructs);
                    WalkExpression(assignment.Value, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeSwitchExpression switchExpression:
                    WalkExpression(switchExpression.Subject, depth, loopHeader, loops, constructs);
                    foreach (var arm in switchExpression.Arms)
                    {
                        WalkExpression(arm.Guard, depth, loopHeader, loops, constructs);
                        WalkExpression(arm.Value, depth, loopHeader, loops, constructs);
                    }

                    return;

                case MetacodeWithExpression withExpression:
                    WalkExpression(withExpression.Target, depth, loopHeader, loops, constructs);
                    foreach (var initializer in withExpression.Initializers)
                        WalkExpression(initializer.Value, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeSpread spread:
                    WalkExpression(spread.Operand, depth, loopHeader, loops, constructs);
                    return;

                case MetacodePatternMatch match:
                    WalkExpression(match.Operand, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeRange range:
                    WalkExpression(range.Start, depth, loopHeader, loops, constructs);
                    WalkExpression(range.End, depth, loopHeader, loops, constructs);
                    WalkExpression(range.Step, depth, loopHeader, loops, constructs);
                    return;

                case MetacodeIndexFromEnd fromEnd:
                    WalkExpression(fromEnd.Operand, depth, loopHeader, loops, constructs);
                    return;
            }
        }

        /// <summary>A call: a LINQ materialiser/deferred operator, a synchronous wait, or both.</summary>
        private void RecordInvocation(
            MetacodeInvocation invocation,
            int depth,
            string? loopHeader,
            List<CodeConstructInfo> constructs)
        {
            if (invocation.Callee is not MetacodeMemberAccess access) return;

            var member = access.Member;

            if (member is "Wait" or "GetResult")
            {
                constructs.Add(new CodeConstructInfo(
                    "sync-over-async", Describe(invocation),
                    invocation.Origin.StartLine, EndLine(invocation.Origin), depth, loopHeader));
                return;
            }

            if (Materialisers.Contains(member))
            {
                constructs.Add(new CodeConstructInfo(
                    "linq", $"{member}() materialises {Describe(access.Target)}",
                    invocation.Origin.StartLine, EndLine(invocation.Origin), depth, loopHeader));
                return;
            }

            if (Deferred.Contains(member))
            {
                constructs.Add(new CodeConstructInfo(
                    "linq", $"{member}() over {Describe(access.Target)}",
                    invocation.Origin.StartLine, EndLine(invocation.Origin), depth, loopHeader));
            }
        }

        private static bool LooksLikeString(MetacodeBinary binary) =>
            ContainsString(binary.Left) || ContainsString(binary.Right);

        private static bool ContainsString(MetacodeExpression expression) => expression switch
        {
            MetacodeLiteral { Kind: MetacodeLiteralKind.String or MetacodeLiteralKind.Character } => true,
            MetacodeInterpolatedString => true,
            MetacodeBinary { Operator: MetacodeBinaryOperator.Add } binary =>
                ContainsString(binary.Left) || ContainsString(binary.Right),
            _ => false,
        };

        /// <summary>The source line of a construct, with the trailing brace removed (a loop header reads better without it).</summary>
        private string? HeaderAt(int line)
        {
            if (line <= 0 || line > lines.Length) return null;

            var text = lines[line - 1].Trim();
            text = text.TrimEnd('{', ' ');
            if (text.Length == 0) return null;
            return text.Length <= 140 ? text : text[..140] + "…";
        }

        private static int EndLine(MetacodeOrigin origin) =>
            origin.EndLine >= origin.StartLine ? origin.EndLine : origin.StartLine;

        private static string Describe(MetacodeExpression? expression) => expression switch
        {
            null => string.Empty,
            MetacodeIdentifier identifier => identifier.Name,
            MetacodeLiteral literal => literal.Text,
            MetacodeMemberAccess access => $"{Describe(access.Target)}.{access.Member}",
            MetacodeInvocation invocation => $"{Describe(invocation.Callee)}(…)",
            MetacodeObjectCreation creation => "new " + creation.Type.Render(),
            MetacodeNewArray array => "new " + (array.ElementType?.Render() ?? "array") + "[]",
            MetacodeAwait awaitExpression => "await " + Describe(awaitExpression.Operand),
            MetacodeCast cast => "(" + cast.Type.Render() + ")" + Describe(cast.Operand),
            MetacodeInterpolatedString interpolated => "$" + $"\"…{interpolated.Parts.Count} part(s)\"",
            MetacodeBinary binary => $"{Describe(binary.Left)} {Operator(binary.Operator)} {Describe(binary.Right)}",
            MetacodeIndex index => $"{Describe(index.Target)}[…]",
            MetacodeConditional conditional => $"{Describe(conditional.Condition)} ? … : …",
            MetacodeComprehension => "comprehension",
            _ => expression.GetType().Name.Replace("Metacode", string.Empty),
        };

        private static string Operator(MetacodeBinaryOperator op) => op switch
        {
            MetacodeBinaryOperator.Add => "+",
            MetacodeBinaryOperator.Subtract => "-",
            MetacodeBinaryOperator.Multiply => "*",
            MetacodeBinaryOperator.Divide => "/",
            MetacodeBinaryOperator.Modulo => "%",
            MetacodeBinaryOperator.Equal => "==",
            MetacodeBinaryOperator.NotEqual => "!=",
            MetacodeBinaryOperator.LogicalAnd => "&&",
            MetacodeBinaryOperator.LogicalOr => "||",
            MetacodeBinaryOperator.NullCoalesce => "??",
            _ => op.ToString(),
        };
    }
}
