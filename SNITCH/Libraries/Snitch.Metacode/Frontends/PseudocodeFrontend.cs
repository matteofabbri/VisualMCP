using Snitch.Metacode.Rendering;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads the canonical pseudocode that <see cref="PseudocodeRenderer"/> prints. This is the door
/// every other language comes through: a producer that can emit this text — a disassembler, a DSL, a
/// language this library has no frontend for — joins the same model without touching the translator.
/// </summary>
public sealed class PseudocodeFrontend : ILanguageFrontend
{
    public string Name => "Pseudocode";

    public MetacodeLanguage Language => MetacodeLanguage.Pseudocode;

    public bool CanHandle(MetacodeSource source) =>
        source.Language == MetacodeLanguage.Pseudocode
        || (source.Language is null && source.Path is not null
            && (source.Path.EndsWith(".mc", StringComparison.OrdinalIgnoreCase)
                || source.Path.EndsWith(".pseudo", StringComparison.OrdinalIgnoreCase)
                || source.Path.EndsWith(".metacode", StringComparison.OrdinalIgnoreCase)));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options)
    {
        var diagnostics = new List<MetacodeDiagnostic>();
        var modules = new PseudocodeParser(source.Text, diagnostics).Parse();
        return new MetacodeDocument(source.Name, MetacodeLanguage.Pseudocode, modules, diagnostics);
    }
}

/// <summary>The line-oriented parser. Every construct the renderer prints can be read back.</summary>
internal sealed class PseudocodeParser
{
    private const string Modifiers =
        "PUBLIC PROTECTED INTERNAL PRIVATE NEW STATIC ABSTRACT VIRTUAL OVERRIDE SEALED READONLY CONST ASYNC PARTIAL EXTERN UNSAFE";

    private static readonly string[] FunctionKeywords =
    {
        "LOCAL FUNCTION", "FUNCTION", "METHOD", "CONSTRUCTOR", "DESTRUCTOR", "OPERATOR",
    };

    private static readonly string[] TypeKeywords =
    {
        "CLASS", "STRUCT", "INTERFACE", "ENUM", "RECORD", "UNION", "TRAIT",
    };

    private readonly string[] _lines;
    private readonly List<MetacodeDiagnostic> _diagnostics;
    private readonly List<string> _decorators = new();
    private int _cursor;

    public PseudocodeParser(string text, List<MetacodeDiagnostic> diagnostics)
    {
        _lines = text.Replace("\r", string.Empty).Split('\n');
        _diagnostics = diagnostics;
    }

    public IReadOnlyList<MetacodeModule> Parse()
    {
        var modules = new List<MetacodeModule>();
        while (true)
        {
            var line = Peek();
            if (line is null)
                return modules;

            Take();
            if (line.StartsWith("//", StringComparison.Ordinal) || line.Length == 0)
                continue;

            if (line.StartsWith("MODULE ", StringComparison.Ordinal) || line.StartsWith("NAMESPACE ", StringComparison.Ordinal))
            {
                modules.Add(Module(line));
                continue;
            }

            Report(MetacodeDiagnosticCodes.ParseError, "expected MODULE or NAMESPACE, found: " + line);
        }
    }

    private MetacodeModule Module(string header)
    {
        var isNamespace = header.StartsWith("NAMESPACE ", StringComparison.Ordinal);
        var keyword = isNamespace ? "NAMESPACE" : "MODULE";
        var name = header[keyword.Length..].Trim();
        var declarations = new List<MetacodeDeclaration>();
        while (true)
        {
            var line = Peek();
            if (line is null)
            {
                Report(MetacodeDiagnosticCodes.ParseError, $"module '{name}' is not closed with END {keyword}");
                break;
            }

            if (line == "END " + keyword)
            {
                Take();
                break;
            }

            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                Take();
                continue;
            }

            if (IsDecoratorLine(line))
            {
                Take();
                _decorators.Add(line[1..].Trim());
                continue;
            }

            Take();
            var decorators = TakeDecorators();
            var declaration = Declaration(JoinContinuation(line));
            if (declaration is not null)
                declarations.Add(ApplyDecorators(declaration, decorators));
        }

        ReportPendingDecorators();
        return new MetacodeModule(name, declarations, isNamespace ? MetacodeModuleKind.Namespace : MetacodeModuleKind.Module);
    }

    private MetacodeDeclaration? Declaration(string line)
    {
        if (line.StartsWith("RAW[", StringComparison.Ordinal))
            return OpaqueDeclaration(line);

        var (modifiers, rest) = SplitModifiers(line);
        var word = FirstWord(rest);

        foreach (var keyword in TypeKeywords)
        {
            if (word == keyword)
                return Type(rest, keyword, modifiers);
        }

        foreach (var keyword in FunctionKeywords)
        {
            if (rest.StartsWith(keyword + " ", StringComparison.Ordinal))
                return Function(rest, keyword, modifiers);
        }

        if (rest.StartsWith("PROPERTY ", StringComparison.Ordinal))
            return Property(rest, modifiers);

        if (rest.StartsWith("EVENT ", StringComparison.Ordinal))
            return Event(rest, modifiers);

        if (rest.StartsWith("ALIAS ", StringComparison.Ordinal))
            return Alias(rest);

        if (rest.StartsWith("FIELD ", StringComparison.Ordinal) || modifiers.HasFlag(MetacodeModifiers.Const))
            return Field(rest, modifiers);

        // An enum body is a list of bare names; anything else here is a line the parser does not know.
        if (_enumDepth > 0)
            return EnumMember(line);

        Report(MetacodeDiagnosticCodes.ParseError, "unrecognised declaration: " + line);
        return null;
    }

    private int _enumDepth;

    private MetacodeTypeDeclaration Type(string header, string keyword, MetacodeModifiers modifiers)
    {
        var rest = header[keyword.Length..].Trim();
        var name = ReadName(ref rest);
        var typeParameters = ReadTypeParameters(ref rest);

        var baseTypes = new List<MetacodeTypeRef>();
        if (rest.StartsWith("EXTENDS ", StringComparison.Ordinal))
        {
            baseTypes.AddRange(SplitTopLevel(rest["EXTENDS ".Length..], ',').Select(part => MetacodeTypeRef.Of(part.Trim())));
        }

        var kind = keyword switch
        {
            "STRUCT" => MetacodeTypeKind.Struct,
            "INTERFACE" => MetacodeTypeKind.Interface,
            "ENUM" => MetacodeTypeKind.Enum,
            "RECORD" => MetacodeTypeKind.Record,
            "UNION" => MetacodeTypeKind.Union,
            "TRAIT" => MetacodeTypeKind.Trait,
            _ => MetacodeTypeKind.Class,
        };

        var members = new List<MetacodeDeclaration>();
        var isEnum = kind == MetacodeTypeKind.Enum;
        if (isEnum)
            _enumDepth++;

        while (true)
        {
            var line = Peek();
            if (line is null)
            {
                Report(MetacodeDiagnosticCodes.ParseError, $"type '{name}' is not closed with END {keyword}");
                break;
            }

            if (line == "END " + keyword)
            {
                Take();
                break;
            }

            Take();
            if (line.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (IsDecoratorLine(line))
            {
                _decorators.Add(line[1..].Trim());
                continue;
            }

            var decorators = TakeDecorators();
            var member = isEnum ? EnumMember(line) : Declaration(JoinContinuation(line));
            if (member is not null)
                members.Add(ApplyDecorators(member, decorators));
        }

        if (isEnum)
            _enumDepth--;

        ReportPendingDecorators();
        return new MetacodeTypeDeclaration(name, kind, members, modifiers, baseTypes.Count == 0 ? null : baseTypes, typeParameters);
    }

    /// <summary>
    /// Takes the <c>@</c> lines that preceded a declaration. They are taken before the declaration is
    /// parsed: its body is a block of its own, and that block ends by reporting what it never attached.
    /// </summary>
    private IReadOnlyList<string>? TakeDecorators()
    {
        if (_decorators.Count == 0)
            return null;

        var decorators = _decorators.ToList();
        _decorators.Clear();
        return decorators;
    }

    /// <summary>
    /// Whether a line is a decorator rather than a statement: C# spells an escaped identifier
    /// <c>@event</c>, so <c>@</c> alone does not decide it. A decorator is a name path or a call of one —
    /// it assigns nothing and increments nothing.
    /// </summary>
    private static bool IsDecoratorLine(string line) =>
        line.StartsWith('@')
        && !line.EndsWith("++", StringComparison.Ordinal)
        && !line.EndsWith("--", StringComparison.Ordinal)
        && TopLevelAssignmentIndex(line) < 0;

    /// <summary>Attaches decorators to the declaration they decorate; a decorator needs one to land on.</summary>
    private MetacodeDeclaration ApplyDecorators(MetacodeDeclaration declaration, IReadOnlyList<string>? decorators)
    {
        if (decorators is null || decorators.Count == 0)
            return declaration;

        return declaration switch
        {
            MetacodeFunction function => function with { Decorators = decorators },
            MetacodeTypeDeclaration type => type with { Decorators = decorators },
            _ => ReportLostDecorators(declaration, decorators),
        };
    }

    private MetacodeDeclaration ReportLostDecorators(MetacodeDeclaration declaration, IReadOnlyList<string> decorators)
    {
        Report(MetacodeDiagnosticCodes.ParseError, "a decorator decorates nothing that can carry it: " + string.Join(", ", decorators));
        return declaration;
    }

    private MetacodeEnumMember EnumMember(string line)
    {
        var (name, value) = SplitAssignment(line);
        return new MetacodeEnumMember(name.Trim(), value is null ? null : Expression(value));
    }

    private MetacodeFunction Function(string header, string keyword, MetacodeModifiers modifiers)
    {
        var rest = header[keyword.Length..].Trim();
        var name = keyword == "OPERATOR" ? ReadUntil(ref rest, '(') : ReadName(ref rest);
        var typeParameters = ReadTypeParameters(ref rest);

        var parameters = new List<MetacodeParameter>();
        if (rest.StartsWith('('))
        {
            var close = MatchingIndex(rest, 0, '(', ')');
            if (close < 0)
            {
                Report(MetacodeDiagnosticCodes.ParseError, "a parameter list is not closed: " + header);
                rest = string.Empty;
            }
            else
            {
                parameters = SplitTopLevel(rest[1..close], ',').Where(p => p.Trim().Length > 0).Select(Parameter).ToList();
                rest = rest[(close + 1)..].Trim();
            }
        }

        MetacodeTypeRef? returnType = null;
        if (rest.StartsWith(':'))
            returnType = MetacodeTypeRef.Of(rest[1..].Trim());

        var kind = keyword switch
        {
            "METHOD" => MetacodeFunctionKind.Method,
            "CONSTRUCTOR" => MetacodeFunctionKind.Constructor,
            "DESTRUCTOR" => MetacodeFunctionKind.Destructor,
            "OPERATOR" => MetacodeFunctionKind.Operator,
            "LOCAL FUNCTION" => MetacodeFunctionKind.LocalFunction,
            _ => MetacodeFunctionKind.Function,
        };

        var endKeyword = keyword == "LOCAL FUNCTION" ? "FUNCTION" : keyword;
        var body = new MetacodeBlock(Statements(
            line => line == "END " + endKeyword,
            keyword.ToLowerInvariant() + " " + name));

        var next = Peek();
        if (next == "END " + endKeyword)
            Take();
        else
            Report(MetacodeDiagnosticCodes.ParseError, $"function '{name}' is not closed with END {endKeyword}");

        return new MetacodeFunction(name, kind, parameters, returnType, body, modifiers, typeParameters);
    }

    private MetacodeProperty Property(string header, MetacodeModifiers modifiers)
    {
        var rest = header["PROPERTY ".Length..].Trim();
        var name = ReadName(ref rest);

        var parameters = new List<MetacodeParameter>();
        if (rest.StartsWith('['))
        {
            var close = MatchingIndex(rest, 0, '[', ']');
            if (close > 0)
            {
                parameters = SplitTopLevel(rest[1..close], ',').Where(p => p.Trim().Length > 0).Select(Parameter).ToList();
                rest = rest[(close + 1)..].Trim();
            }
        }

        MetacodeTypeRef? type = null;
        MetacodeExpression? initializer = null;
        if (rest.StartsWith(':'))
        {
            var (typeText, remainder) = SplitAtEquals(rest[1..]);
            type = MetacodeTypeRef.Of(typeText.Trim());
            rest = remainder;
        }

        if (rest.StartsWith('='))
            initializer = Expression(rest[1..]);

        MetacodeAccessor? getter = null;
        MetacodeAccessor? setter = null;
        while (true)
        {
            var line = Peek();
            if (line is null || line == "END PROPERTY")
            {
                if (line is not null)
                    Take();
                break;
            }

            Take();
            var (accessorModifiers, accessorRest) = SplitModifiers(line);
            if (accessorRest.StartsWith("GET", StringComparison.Ordinal))
                getter = Accessor(accessorRest, "GET", accessorModifiers);
            else if (accessorRest.StartsWith("SET", StringComparison.Ordinal))
                setter = Accessor(accessorRest, "SET", accessorModifiers);
            else
                Report(MetacodeDiagnosticCodes.ParseError, "unrecognised property accessor: " + line);
        }

        return new MetacodeProperty(name, type, modifiers, getter, setter, initializer, parameters.Count == 0 ? null : parameters);
    }

    private MetacodeAccessor Accessor(string header, string keyword, MetacodeModifiers modifiers)
    {
        var rest = header[keyword.Length..].Trim();
        string? parameterName = null;
        if (rest.StartsWith('('))
        {
            var close = rest.IndexOf(')');
            parameterName = close > 1 ? rest[1..close].Trim() : null;
            rest = close >= 0 ? rest[(close + 1)..].Trim() : rest;
        }

        if (rest.StartsWith("AUTO", StringComparison.Ordinal))
            return new MetacodeAccessor(modifiers);

        var body = new MetacodeBlock(Statements(line => line == "END " + keyword, keyword.ToLowerInvariant()));
        var next = Peek();
        if (next == "END " + keyword)
            Take();
        else
            Report(MetacodeDiagnosticCodes.ParseError, $"accessor {keyword} is not closed with END {keyword}");

        return new MetacodeAccessor(modifiers, body, parameterName);
    }

    private MetacodeField Field(string header, MetacodeModifiers modifiers)
    {
        var rest = header;
        if (rest.StartsWith("FIELD ", StringComparison.Ordinal))
            rest = rest["FIELD ".Length..];

        var (left, value) = SplitAssignment(rest);
        var name = left.Trim();
        MetacodeTypeRef? type = null;
        var colon = TopLevelIndex(name, ':');
        if (colon >= 0)
        {
            type = MetacodeTypeRef.Of(name[(colon + 1)..].Trim());
            name = name[..colon].Trim();
        }

        return new MetacodeField(name, type, modifiers, value is null ? null : Expression(value));
    }

    /// <summary><c>PUBLIC EVENT Changed : EventHandler</c>.</summary>
    private MetacodeEvent Event(string header, MetacodeModifiers modifiers)
    {
        var rest = header["EVENT ".Length..];
        var (left, _) = SplitAssignment(rest);
        var name = left.Trim();
        MetacodeTypeRef? type = null;
        var colon = TopLevelIndex(name, ':');
        if (colon >= 0)
        {
            type = MetacodeTypeRef.Of(name[(colon + 1)..].Trim());
            name = name[..colon].Trim();
        }

        return new MetacodeEvent(name, type, modifiers);
    }

    /// <summary><c>ALIAS Count = int</c>.</summary>
    private MetacodeTypeAlias Alias(string header)
    {
        var (left, value) = SplitAssignment(header["ALIAS ".Length..]);
        return new MetacodeTypeAlias(left.Trim(), value is null ? null : MetacodeTypeRef.Of(value));
    }

    private List<MetacodeStatement> Statements(Func<string, bool> isEnd, string context)
    {
        var statements = new List<MetacodeStatement>();
        while (true)
        {
            var line = Peek();
            if (line is null)
            {
                Report(MetacodeDiagnosticCodes.ParseError, $"unterminated {context}");
                ReportPendingDecorators();
                return statements;
            }

            if (isEnd(line))
            {
                ReportPendingDecorators();
                return statements;
            }

            Take();
            var statement = Statement(JoinContinuation(line));
            if (statement is not null)
                statements.Add(statement);
        }
    }

    /// <summary>A decorator that never reached a declaration is reported instead of disappearing.</summary>
    private void ReportPendingDecorators()
    {
        if (_decorators.Count == 0)
            return;

        Report(MetacodeDiagnosticCodes.ParseError, "a decorator decorates nothing that can carry it: " + string.Join(", ", _decorators));
        _decorators.Clear();
    }

    /// <summary>
    /// Reads statements to the end of the text. A lambda body is a block of statements inside an
    /// expression, so it needs the statement reader without the line structure of an enclosing block.
    /// </summary>
    internal IReadOnlyList<MetacodeStatement> ParseBody()
    {
        var statements = new List<MetacodeStatement>();
        while (true)
        {
            var line = Peek();
            if (line is null)
            {
                ReportPendingDecorators();
                return statements;
            }

            Take();
            var statement = Statement(JoinContinuation(line));
            if (statement is not null)
                statements.Add(statement);
        }
    }

    /// <summary>
    /// Appends the lines a statement continues on. A block-bodied lambda is a block inside an
    /// expression, so its braces — and the parentheses around a call that holds it — are open until a
    /// later line closes them; those lines belong to the same statement.
    /// </summary>
    private string JoinContinuation(string line)
    {
        if (line.StartsWith("RAW[", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
            return line;

        var text = line;
        while (!IsBalanced(text))
        {
            var next = Take();
            if (next is null)
                break;
            text += "\n" + next;
        }

        return text;
    }

    /// <summary>Whether every bracket the line opens is closed before it ends.</summary>
    private static bool IsBalanced(string text)
    {
        var depth = 0;
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            if (c is '"' or '\'')
            {
                index++;
                while (index < text.Length)
                {
                    if (text[index] == '\\')
                    {
                        index += 2;
                        continue;
                    }

                    if (text[index] == c)
                        break;

                    index++;
                }

                index++;
                continue;
            }

            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;

            index++;
        }

        return depth <= 0;
    }

    private MetacodeStatement? Statement(string line)
    {
        if (line.StartsWith("//", StringComparison.Ordinal))
            return new MetacodeComment(line[2..].Trim());

        // A decorator in statement position can only decorate a local function; it waits for one.
        if (IsDecoratorLine(line))
        {
            _decorators.Add(line[1..].Trim());
            return null;
        }

        if (line.StartsWith("RAW[", StringComparison.Ordinal))
            return OpaqueStatement(line);

        if (line.StartsWith("DECLARE ", StringComparison.Ordinal))
            return LocalVariable(line["DECLARE ".Length..]);

        if (line.StartsWith("CONST ", StringComparison.Ordinal))
            return LocalVariable(line["CONST ".Length..], isConstant: true);

        if (line.StartsWith("IF ", StringComparison.Ordinal) && line.EndsWith(" THEN", StringComparison.Ordinal))
            return If(line);

        if (line.StartsWith("WHILE ", StringComparison.Ordinal) && line.EndsWith(" DO", StringComparison.Ordinal))
        {
            var condition = Expression(line["WHILE ".Length..^" DO".Length]);
            var body = new MetacodeBlock(Statements(l => l == "END WHILE", "while"));
            Expect("END WHILE");
            return new MetacodeWhile(condition, body);
        }

        if (line == "REPEAT")
        {
            var body = new MetacodeBlock(Statements(l => l.StartsWith("UNTIL ", StringComparison.Ordinal), "repeat"));
            var until = Take() ?? "UNTIL true";
            return new MetacodeDoWhile(body, Expression(until["UNTIL ".Length..]));
        }

        if (line.StartsWith("AWAIT FOREACH ", StringComparison.Ordinal) && line.EndsWith(" DO", StringComparison.Ordinal))
            return ForEach(line["AWAIT FOREACH ".Length..^" DO".Length], isAwait: true);

        if (line.StartsWith("FOREACH ", StringComparison.Ordinal) && line.EndsWith(" DO", StringComparison.Ordinal))
            return ForEach(line["FOREACH ".Length..^" DO".Length]);

        if (line.StartsWith("FOR ", StringComparison.Ordinal) && line.EndsWith(" DO", StringComparison.Ordinal))
            return For(line["FOR ".Length..^" DO".Length]);

        if (line.StartsWith("SWITCH ", StringComparison.Ordinal))
            return Switch(line["SWITCH ".Length..]);

        if (line.StartsWith("TRY", StringComparison.Ordinal))
            return Try();

        if (line.StartsWith("USING ", StringComparison.Ordinal) && line.EndsWith(" DO", StringComparison.Ordinal))
            return Using(line["USING ".Length..^" DO".Length]);

        if (line.StartsWith("LOCK ", StringComparison.Ordinal) && line.EndsWith(" DO", StringComparison.Ordinal))
        {
            var target = Expression(line["LOCK ".Length..^" DO".Length]);
            var body = new MetacodeBlock(Statements(l => l == "END LOCK", "lock"));
            Expect("END LOCK");
            return new MetacodeLock(target, body);
        }

        if (line == "YIELD BREAK")
            return new MetacodeYield();

        if (line.StartsWith("YIELD RETURN ", StringComparison.Ordinal))
            return new MetacodeYield(Expression(line["YIELD RETURN ".Length..]));

        if (line.StartsWith("IMPORT ", StringComparison.Ordinal))
            return Import(line["IMPORT ".Length..], isFrom: false);

        if (line.StartsWith("FROM ", StringComparison.Ordinal))
            return Import(line["FROM ".Length..], isFrom: true);

        if (line.StartsWith("RETURN", StringComparison.Ordinal))
            return new MetacodeReturn(line.Length > 6 ? Expression(line[6..].Trim()) : null);

        if (line.StartsWith("THROW", StringComparison.Ordinal))
            return new MetacodeThrow(line.Length > 5 ? Expression(line[5..].Trim()) : null);

        if (line == "BREAK")
            return new MetacodeBreak();

        if (line == "CONTINUE")
            return new MetacodeContinue();

        if (line.StartsWith("GOTO ", StringComparison.Ordinal))
            return new MetacodeGoto(line["GOTO ".Length..].Trim());

        if (line.StartsWith("LABEL ", StringComparison.Ordinal))
        {
            var label = line["LABEL ".Length..].Trim();
            var inner = new MetacodeBlock(Statements(l => l == "END LABEL", "label " + label));
            Expect("END LABEL");
            return new MetacodeLabel(label, inner.Statements.Count == 1 ? inner.Statements[0] : inner);
        }

        if (line.StartsWith("LOCAL FUNCTION ", StringComparison.Ordinal))
        {
            var decorators = TakeDecorators();
            var function = Function(line, "LOCAL FUNCTION", MetacodeModifiers.None);
            return new MetacodeLocalFunction(
                decorators is null ? function : function with { Decorators = decorators });
        }

        // A line that starts like an assignment is one; anything else is an expression statement.
        var (left, value) = SplitAssignment(line);
        if (value is not null && IsAssignable(left))
        {
            var target = Expression(left);
            var @operator = AssignmentOperatorOf(line, left);
            return new MetacodeAssignment(target, @operator, Expression(value));
        }

        return new MetacodeExpressionStatement(Expression(line));
    }

    private static bool IsAssignable(string left) => left.Trim().Length > 0;

    private MetacodeIf If(string header)
    {
        var condition = Expression(header["IF ".Length..^" THEN".Length]);
        var then = new MetacodeBlock(Statements(IsIfBranchEnd, "if"));

        var next = Peek();
        if (next == "ELSE")
        {
            Take();
            var @else = new MetacodeBlock(Statements(l => l == "END IF", "else"));
            Expect("END IF");
            return new MetacodeIf(condition, then, @else);
        }

        if (next is not null && next.StartsWith("ELSE IF ", StringComparison.Ordinal))
        {
            Take();
            var nested = If("IF " + next["ELSE IF ".Length..]);
            return new MetacodeIf(condition, then, MetacodeBlock.Of(nested));
        }

        Expect("END IF");
        return new MetacodeIf(condition, then);
    }

    private static bool IsIfBranchEnd(string line) =>
        line == "ELSE" || line == "END IF" || line.StartsWith("ELSE IF ", StringComparison.Ordinal);

    private MetacodeStatement ForEach(string header, bool isAwait = false)
    {
        var inIndex = TopLevelIndexOfWord(header, "IN");
        if (inIndex < 0)
        {
            Report(MetacodeDiagnosticCodes.ParseError, "foreach without IN: " + header);
            return MetacodeBlock.Empty;
        }

        var variable = header[..inIndex].Trim();
        var iterable = Expression(header[(inIndex + "IN".Length)..].Trim());

        MetacodeTypeRef? type = null;
        var colon = TopLevelIndex(variable, ':');
        if (colon >= 0)
        {
            type = MetacodeTypeRef.Of(variable[(colon + 1)..].Trim());
            variable = variable[..colon].Trim();
        }

        var body = new MetacodeBlock(Statements(l => l == "END FOREACH", "foreach"));
        Expect("END FOREACH");
        return new MetacodeForEach(variable, type, iterable, body, isAwait);
    }

    /// <summary><c>IMPORT os, numpy AS np</c> or <c>FROM typing IMPORT List</c>.</summary>
    private MetacodeStatement Import(string header, bool isFrom)
    {
        string? module = null;
        var names = new List<MetacodeImportName>();
        var list = header;

        if (isFrom)
        {
            var index = list.IndexOf(" IMPORT ", StringComparison.Ordinal);
            if (index < 0)
            {
                Report(MetacodeDiagnosticCodes.ParseError, "from without import: " + header);
                return MetacodeBlock.Empty;
            }

            module = list[..index].Trim();
            list = list[(index + " IMPORT ".Length)..];
        }

        foreach (var part in SplitTopLevel(list, ','))
        {
            var text = part.Trim();
            if (text.Length == 0)
                continue;

            var alias = text.IndexOf(" AS ", StringComparison.Ordinal);
            names.Add(alias < 0
                ? new MetacodeImportName(text)
                : new MetacodeImportName(text[..alias].Trim(), text[(alias + " AS ".Length)..].Trim()));
        }

        return new MetacodeImport(names, module, isFrom);
    }

    private MetacodeStatement For(string header)
    {
        // A header is three statements, not a type: `i < n` must not open an angle bracket.
        var parts = SplitTopLevel(header, ';', trackAngles: false).ToList();
        if (parts.Count != 3)
        {
            Report(MetacodeDiagnosticCodes.ParseError, "for needs three parts: " + header);
            return MetacodeBlock.Empty;
        }

        var body = new MetacodeBlock(Statements(l => l == "END FOR", "for"));
        Expect("END FOR");

        return new MetacodeFor(
            parts[0].Trim().Length == 0 ? null : Statement(parts[0].Trim()),
            parts[1].Trim().Length == 0 ? null : Expression(parts[1]),
            parts[2].Trim().Length == 0 ? null : Statement(parts[2].Trim()),
            body);
    }

    private MetacodeStatement Switch(string subject)
    {
        var cases = new List<MetacodeSwitchCase>();
        var labels = new List<MetacodeExpression>();
        var isDefault = false;
        var body = new List<MetacodeStatement>();

        void Flush()
        {
            if (labels.Count == 0 && !isDefault)
                return;
            cases.Add(new MetacodeSwitchCase(labels, new MetacodeBlock(body.ToList()), isDefault));
            labels = new List<MetacodeExpression>();
            isDefault = false;
            body = new List<MetacodeStatement>();
        }

        while (true)
        {
            var line = Peek();
            if (line is null || line == "END SWITCH")
            {
                if (line is not null)
                    Take();
                Flush();
                break;
            }

            Take();
            if (line.StartsWith("CASE ", StringComparison.Ordinal))
            {
                Flush();
                labels.AddRange(SplitTopLevel(line["CASE ".Length..], ',').Select(part => Expression(part)));
                continue;
            }

            if (line == "DEFAULT")
            {
                Flush();
                isDefault = true;
                continue;
            }

            var statement = Statement(JoinContinuation(line));
            if (statement is not null)
                body.Add(statement);
        }

        return new MetacodeSwitch(Expression(subject), cases);
    }

    private MetacodeStatement Try()
    {
        var body = new MetacodeBlock(Statements(l => l.StartsWith("CATCH", StringComparison.Ordinal) || l == "ELSE" || l == "FINALLY" || l == "END TRY", "try"));
        var catches = new List<MetacodeCatch>();

        while (true)
        {
            var line = Peek();
            if (line is null)
                break;

            if (line.StartsWith("CATCH", StringComparison.Ordinal))
            {
                Take();
                var rest = line["CATCH".Length..].Trim();
                MetacodeTypeRef? type = null;
                string? variable = null;
                MetacodeExpression? filter = null;

                if (rest.StartsWith('('))
                {
                    var close = rest.IndexOf(')');
                    if (close > 0)
                    {
                        var inner = rest[1..close].Trim();
                        var space = inner.LastIndexOf(' ');
                        if (space > 0)
                        {
                            type = MetacodeTypeRef.Of(inner[..space].Trim());
                            variable = inner[(space + 1)..].Trim();
                        }
                        else if (inner.Length > 0)
                        {
                            type = MetacodeTypeRef.Of(inner);
                        }

                        rest = rest[(close + 1)..].Trim();
                    }
                }

                if (rest.StartsWith("WHEN ", StringComparison.Ordinal))
                    filter = Expression(rest["WHEN ".Length..]);

                var catchBody = new MetacodeBlock(Statements(
                    l => l.StartsWith("CATCH", StringComparison.Ordinal) || l == "ELSE" || l == "FINALLY" || l == "END TRY",
                    "catch"));
                catches.Add(new MetacodeCatch(type, variable, catchBody, filter));
                continue;
            }

            if (line == "ELSE")
            {
                Take();
                var @else = new MetacodeBlock(Statements(l => l == "FINALLY" || l == "END TRY", "else"));
                var @finally = Finally();
                return new MetacodeTry(body, catches, @finally, @else);
            }

            if (line == "FINALLY")
                return new MetacodeTry(body, catches, Finally());

            Expect("END TRY");
            return new MetacodeTry(body, catches);

            MetacodeBlock? Finally()
            {
                MetacodeBlock? block = null;
                if (Peek() == "FINALLY")
                {
                    Take();
                    block = new MetacodeBlock(Statements(l => l == "END TRY", "finally"));
                }

                Expect("END TRY");
                return block;
            }
        }

        return new MetacodeTry(body, catches);
    }

    private MetacodeStatement Using(string header)
    {
        var (left, value) = SplitAssignment(header);
        MetacodeLocalVariable? declaration = null;
        MetacodeExpression? resource = null;

        if (value is not null)
        {
            var name = left.Trim();
            MetacodeTypeRef? type = null;
            var colon = TopLevelIndex(name, ':');
            if (colon >= 0)
            {
                type = MetacodeTypeRef.Of(name[(colon + 1)..].Trim());
                name = name[..colon].Trim();
            }

            declaration = new MetacodeLocalVariable(name, type, Expression(value));
        }
        else
        {
            resource = Expression(header);
        }

        var body = new MetacodeBlock(Statements(l => l == "END USING", "using"));
        Expect("END USING");
        return new MetacodeUsing(declaration, resource, body);
    }

    private MetacodeStatement LocalVariable(string header, bool isConstant = false)
    {
        var (left, value) = SplitAssignment(header);
        var name = left.Trim();
        MetacodeTypeRef? type = null;
        var colon = TopLevelIndex(name, ':');
        if (colon >= 0)
        {
            type = MetacodeTypeRef.Of(name[(colon + 1)..].Trim());
            name = name[..colon].Trim();
        }

        return new MetacodeLocalVariable(name, type, value is null ? null : Expression(value), isConstant);
    }

    private static MetacodeAssignmentOperator AssignmentOperatorOf(string line, string left)
    {
        var rest = line[left.Length..].TrimStart();
        var @operator = rest.Split(' ')[0];
        return @operator switch
        {
            "+=" => MetacodeAssignmentOperator.Add,
            "-=" => MetacodeAssignmentOperator.Subtract,
            "*=" => MetacodeAssignmentOperator.Multiply,
            "/=" => MetacodeAssignmentOperator.Divide,
            "%=" => MetacodeAssignmentOperator.Modulo,
            "**=" => MetacodeAssignmentOperator.Power,
            "&=" => MetacodeAssignmentOperator.BitwiseAnd,
            "|=" => MetacodeAssignmentOperator.BitwiseOr,
            "^=" => MetacodeAssignmentOperator.BitwiseXor,
            "<<=" => MetacodeAssignmentOperator.ShiftLeft,
            ">>=" => MetacodeAssignmentOperator.ShiftRight,
            "??=" => MetacodeAssignmentOperator.Coalesce,
            _ => MetacodeAssignmentOperator.Assign,
        };
    }

    private MetacodeExpression Expression(string text) => PseudocodeExpression.Parse(text.Trim(), _diagnostics, ParseBlock);

    /// <summary>The statement reader, handed to the expression parser for a lambda with a block body.</summary>
    private IReadOnlyList<MetacodeStatement> ParseBlock(string text) =>
        new PseudocodeParser(text, _diagnostics).ParseBody();

    // A `RAW[...]` line is valid canonical text — the renderer writes it for a construct no node
    // covers — so reading it back is a warning about what the line holds, not a failure to parse.

    private MetacodeStatement OpaqueStatement(string line)
    {
        var (language, text, reason) = ParseOpaque(line);
        Warn(MetacodeDiagnosticCodes.UnsupportedStatement, "kept as opaque: " + reason);
        return new MetacodeOpaqueStatement(language, text, reason);
    }

    private MetacodeDeclaration OpaqueDeclaration(string line)
    {
        var (language, text, reason) = ParseOpaque(line);
        Warn(MetacodeDiagnosticCodes.UnsupportedDeclaration, "kept as opaque: " + reason);
        return new MetacodeOpaqueDeclaration(language, text, reason);
    }

    private static (MetacodeLanguage Language, string Text, string Reason) ParseOpaque(string line)
    {
        var close = line.IndexOf(']');
        var tag = close > 4 ? line[4..close] : "?";
        var payload = close >= 0 ? line[(close + 1)..].TrimStart() : line;
        var reason = string.Empty;
        var separator = payload.LastIndexOf(" // ", StringComparison.Ordinal);
        if (separator >= 0)
        {
            reason = payload[(separator + 4)..];
            payload = payload[..separator];
        }

        return (PseudocodeRenderer.LanguageFromTag(tag), payload.Replace("\\n", "\n"), reason);
    }

    private void Expect(string line)
    {
        if (Peek() == line)
        {
            Take();
            return;
        }

        Report(MetacodeDiagnosticCodes.ParseError, "expected " + line);
    }

    private string? Peek()
    {
        while (_cursor < _lines.Length)
        {
            var line = _lines[_cursor].TrimEnd();
            if (line.Trim().Length > 0)
                return line.Trim();
            _cursor++;
        }

        return null;
    }

    private string? Take()
    {
        var line = Peek();
        if (line is not null)
            _cursor++;
        return line;
    }

    private void Report(string code, string message) =>
        _diagnostics.Add(new MetacodeDiagnostic(code, MetacodeDiagnosticSeverity.Error, message, new MetacodeOrigin(MetacodeLanguage.Pseudocode)));

    private void Warn(string code, string message) =>
        _diagnostics.Add(new MetacodeDiagnostic(code, MetacodeDiagnosticSeverity.Warning, message, new MetacodeOrigin(MetacodeLanguage.Pseudocode)));

    private static (MetacodeModifiers Modifiers, string Remainder) SplitModifiers(string line)
    {
        var modifiers = MetacodeModifiers.None;
        var rest = line.Trim();
        while (true)
        {
            var word = FirstWord(rest);
            if (word.Length == 0 || !Modifiers.Split(' ').Contains(word) || rest.Length == word.Length)
                break;

            modifiers |= word switch
            {
                "PUBLIC" => MetacodeModifiers.Public,
                "PROTECTED" => MetacodeModifiers.Protected,
                "INTERNAL" => MetacodeModifiers.Internal,
                "PRIVATE" => MetacodeModifiers.Private,
                "NEW" => MetacodeModifiers.New,
                "STATIC" => MetacodeModifiers.Static,
                "ABSTRACT" => MetacodeModifiers.Abstract,
                "VIRTUAL" => MetacodeModifiers.Virtual,
                "OVERRIDE" => MetacodeModifiers.Override,
                "SEALED" => MetacodeModifiers.Sealed,
                "READONLY" => MetacodeModifiers.ReadOnly,
                "CONST" => MetacodeModifiers.Const,
                "ASYNC" => MetacodeModifiers.Async,
                "PARTIAL" => MetacodeModifiers.Partial,
                "EXTERN" => MetacodeModifiers.Extern,
                "UNSAFE" => MetacodeModifiers.Unsafe,
                _ => MetacodeModifiers.None,
            };
            rest = rest[word.Length..].TrimStart();
        }

        return (modifiers, rest);
    }

    private static string FirstWord(string text)
    {
        var end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            end++;
        return text[..end];
    }

    private static string ReadName(ref string rest)
    {
        rest = rest.TrimStart();
        var end = 0;
        while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] is '_' or '.'))
            end++;

        var name = rest[..end];
        rest = rest[end..].TrimStart();
        return name;
    }

    private static string ReadUntil(ref string rest, char stop)
    {
        rest = rest.TrimStart();
        var end = rest.IndexOf(stop);
        if (end < 0)
            end = rest.Length;
        var name = rest[..end].Trim();
        rest = rest[end..].TrimStart();
        return name;
    }

    private static List<MetacodeTypeParameter>? ReadTypeParameters(ref string rest)
    {
        if (!rest.StartsWith('<'))
            return null;

        var close = MatchingIndex(rest, 0, '<', '>');
        if (close < 0)
            return null;

        var inner = rest[1..close];
        rest = rest[(close + 1)..].TrimStart();
        return SplitTopLevel(inner, ',').Select(part =>
        {
            var text = part.Trim();
            var colon = text.IndexOf(':');
            if (colon < 0)
                return new MetacodeTypeParameter(text);
            var constraints = text[(colon + 1)..].Split('+').Select(c => c.Trim()).ToList();
            return new MetacodeTypeParameter(text[..colon].Trim(), constraints);
        }).ToList();
    }

    private MetacodeParameter Parameter(string text)
    {
        var rest = text.Trim();
        var kind = MetacodeParameterKind.Value;
        foreach (var (prefix, parameterKind) in new[]
                 {
                     ("ref ", MetacodeParameterKind.Ref),
                     ("out ", MetacodeParameterKind.Out),
                     ("in ", MetacodeParameterKind.In),
                     ("params ", MetacodeParameterKind.Params),
                     ("*", MetacodeParameterKind.Keyword),
                 })
        {
            if (!rest.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            kind = parameterKind;
            rest = rest[prefix.Length..];
            break;
        }

        var (left, value) = SplitAssignment(rest);
        var name = left.Trim();
        MetacodeTypeRef? type = null;
        var colon = TopLevelIndex(name, ':');
        if (colon >= 0)
        {
            type = MetacodeTypeRef.Of(name[(colon + 1)..].Trim());
            name = name[..colon].Trim();
        }

        return new MetacodeParameter(name, type, value is null ? null : Expression(value), kind);
    }

    /// <summary>Splits <c>left = right</c> at a top-level assignment, leaving <c>left</c> whole.</summary>
    private static (string Left, string? Right) SplitAssignment(string text)
    {
        var index = TopLevelAssignmentIndex(text);
        if (index < 0)
            return (text, null);

        // The operator ends at the '='; a compound one starts a few characters earlier.
        var start = index;
        while (start > 0 && text[start - 1] is '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' or '?' or '<' or '>')
            start--;

        return (text[..start].Trim(), text[(index + 1)..].Trim());
    }

    private static int TopLevelAssignmentIndex(string text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case '=' when depth == 0:
                    var next = i + 1 < text.Length ? text[i + 1] : ' ';
                    if (next is '=' or '>')
                        break;
                    return i;
            }
        }

        return -1;
    }

    private static (string Type, string Remainder) SplitAtEquals(string text)
    {
        var index = TopLevelAssignmentIndex(text);
        return index < 0 ? (text, string.Empty) : (text[..index], text[index..]);
    }

    private static int TopLevelIndex(string text, char needle)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '[' or '{' or '<':
                    depth++;
                    break;
                case ')' or ']' or '}' or '>':
                    depth--;
                    break;
                default:
                    if (c == needle && depth == 0)
                        return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>The index of a word in the text, or -1. The padding makes the first and last word match.</summary>
    private static int TopLevelIndexOfWord(string text, string word)
    {
        // `index - 1` maps the padded match back to text coordinates; with the leading space removed the
        // word starts at exactly that index.
        var padded = " " + text + " ";
        var index = padded.IndexOf(" " + word + " ", StringComparison.Ordinal);
        return index < 0 ? -1 : index;
    }

    private static int MatchingIndex(string text, int start, char open, char close)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == open)
                depth++;
            else if (c == close && --depth == 0)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Splits on a separator that sits at bracket depth zero. Angle brackets count only where they can
    /// be type arguments (a parameter list); in a statement header <c>&lt;</c> is a comparison, and
    /// counting it as an opening bracket would hide every later separator — which is exactly what a
    /// C-style <c>for</c> header does.
    /// </summary>
    private static IEnumerable<string> SplitTopLevel(string text, char separator, bool trackAngles = true)
    {
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case '<' when trackAngles:
                    depth++;
                    break;
                case '>' when trackAngles:
                    depth--;
                    break;
                default:
                    if (c == separator && depth == 0)
                    {
                        yield return text[start..i];
                        start = i + 1;
                    }

                    break;
            }
        }

        yield return text[start..];
    }
}
