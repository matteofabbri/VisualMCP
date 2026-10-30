namespace Snitch.Metacode;

/// <summary>
/// The statement vocabulary every frontend lowers into. It is smaller than any single language on
/// purpose: a construct that does not fit is either desugared into these (a C-style <c>for</c> becomes
/// a <c>while</c> in <see cref="Passes.NormalizeControlFlowPass"/>) or kept as
/// <see cref="MetacodeOpaqueStatement"/> with a diagnostic — never invented.
/// </summary>
public abstract record MetacodeStatement : MetacodeNode;

/// <summary>A sequence of statements. A block returned by a rewrite is spliced into its parent.</summary>
public sealed record MetacodeBlock(IReadOnlyList<MetacodeStatement> Statements) : MetacodeStatement
{
    public static MetacodeBlock Empty { get; } = new(Array.Empty<MetacodeStatement>());

    public static MetacodeBlock Of(params MetacodeStatement[] statements) => new(statements);
}

/// <summary>A local declaration. The type is null for a language that does not annotate.</summary>
public sealed record MetacodeLocalVariable(
    string Name,
    MetacodeTypeRef? Type = null,
    MetacodeExpression? Initializer = null,
    bool IsConstant = false) : MetacodeStatement;

public enum MetacodeAssignmentOperator
{
    Assign = 0,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Power,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
    Coalesce,
}

/// <summary>An assignment as a statement, simple or compound (<c>x += 1</c>).</summary>
public sealed record MetacodeAssignment(
    MetacodeExpression Target,
    MetacodeAssignmentOperator Operator,
    MetacodeExpression Value) : MetacodeStatement;

public sealed record MetacodeExpressionStatement(MetacodeExpression Expression) : MetacodeStatement;

public sealed record MetacodeReturn(MetacodeExpression? Value = null) : MetacodeStatement;

public sealed record MetacodeIf(
    MetacodeExpression Condition,
    MetacodeBlock Then,
    MetacodeBlock? Else = null) : MetacodeStatement;

public sealed record MetacodeWhile(MetacodeExpression Condition, MetacodeBlock Body) : MetacodeStatement;

/// <summary><c>do { } while (c)</c> / <c>repeat ... until c</c>.</summary>
public sealed record MetacodeDoWhile(MetacodeBlock Body, MetacodeExpression Condition) : MetacodeStatement;

/// <summary>
/// <c>foreach</c> / <c>for … in</c> / a C++ range-based <c>for</c>. <see cref="IsAwait"/> marks the
/// <c>await foreach</c> form, which consumes an asynchronous sequence and would otherwise print like a
/// synchronous one.
/// </summary>
public sealed record MetacodeForEach(
    string VariableName,
    MetacodeTypeRef? VariableType,
    MetacodeExpression Iterable,
    MetacodeBlock Body,
    bool IsAwait = false) : MetacodeStatement;

/// <summary>
/// A counted loop in the C form. A frontend that can see the counter (Python's <c>range</c>) builds
/// this too, so the same loop reads the same way whatever produced it.
/// </summary>
public sealed record MetacodeFor(
    MetacodeStatement? Initializer,
    MetacodeExpression? Condition,
    MetacodeStatement? Increment,
    MetacodeBlock Body) : MetacodeStatement;

public sealed record MetacodeSwitch(
    MetacodeExpression Subject,
    IReadOnlyList<MetacodeSwitchCase> Cases) : MetacodeStatement;

public sealed record MetacodeSwitchCase(
    IReadOnlyList<MetacodeExpression>? Labels,
    MetacodeBlock Body,
    bool IsDefault = false) : MetacodeStatement;

public sealed record MetacodeBreak : MetacodeStatement;

public sealed record MetacodeContinue : MetacodeStatement;

public sealed record MetacodeGoto(string Label) : MetacodeStatement;

/// <summary>A label wrapping the one statement it names (<c>outer: while (...)</c>).</summary>
public sealed record MetacodeLabel(string Label, MetacodeStatement Statement) : MetacodeStatement;

public sealed record MetacodeThrow(MetacodeExpression? Exception = null) : MetacodeStatement;

/// <summary>
/// try / catch / finally. <see cref="Else"/> exists because Python's <c>try/except/else</c> cannot be
/// folded into the try body without changing what is caught.
/// </summary>
public sealed record MetacodeTry(
    MetacodeBlock Body,
    IReadOnlyList<MetacodeCatch> Catches,
    MetacodeBlock? Finally = null,
    MetacodeBlock? Else = null) : MetacodeStatement;

public sealed record MetacodeCatch(
    MetacodeTypeRef? ExceptionType,
    string? VariableName,
    MetacodeBlock Body,
    MetacodeExpression? Filter = null) : MetacodeStatement;

/// <summary><c>using</c> in C#, <c>with</c> in Python: a resource scoped to a block.</summary>
public sealed record MetacodeUsing(
    MetacodeLocalVariable? Declaration,
    MetacodeExpression? Resource,
    MetacodeBlock Body) : MetacodeStatement;

/// <summary>
/// A mutual-exclusion block: C#'s <c>lock (gate) { … }</c>. It is not a <see cref="MetacodeUsing"/>:
/// a lock releases on exit too, but what it protects is the absence of other threads, not a resource,
/// and printing it as <c>USING</c> would say something the source did not.
/// </summary>
public sealed record MetacodeLock(MetacodeExpression Target, MetacodeBlock Body) : MetacodeStatement;

/// <summary>
/// <c>yield return value</c> and <c>yield break</c>: a null <see cref="Value"/> is the break. The
/// distinction matters — one hands a value to the enumerator, the other ends it.
/// </summary>
public sealed record MetacodeYield(MetacodeExpression? Value = null) : MetacodeStatement;

/// <summary>
/// An import: <c>import os</c>, <c>import numpy as np</c>, <c>from typing import List</c>. It is a
/// statement because that is what Python makes of it, even when a file keeps them all at the top.
/// </summary>
public sealed record MetacodeImport(
    IReadOnlyList<MetacodeImportName>? Names = null,
    string? Module = null,
    bool IsFrom = false) : MetacodeStatement
{
    public IReadOnlyList<MetacodeImportName> NameList => Names ?? Array.Empty<MetacodeImportName>();
}

/// <summary>One name of an import, with the local alias when it has one (<c>import numpy as np</c>).</summary>
public sealed record MetacodeImportName(string Name, string? Alias = null) : MetacodeNode;

/// <summary>A function declared inside a body (a local function, a nested <c>def</c>).</summary>
public sealed record MetacodeLocalFunction(MetacodeFunction Function) : MetacodeStatement;

public sealed record MetacodeComment(string Text) : MetacodeStatement;

/// <summary>
/// Code the frontend could not express, kept verbatim with the reason. This is the pressure valve
/// that makes "any language" honest: an unsupported construct is reported, not silently dropped.
/// </summary>
public sealed record MetacodeOpaqueStatement(
    MetacodeLanguage Language,
    string Text,
    string Reason) : MetacodeStatement;
