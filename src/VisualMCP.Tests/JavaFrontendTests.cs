using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The Java reader: the shape JADX actually emits — a package, imports, fields, constructors, methods
/// with the usual control flow, a multi-catch, an enhanced for, an anonymous class — lowered into the
/// shared model. The anonymous class is the one construct with no node: it must survive as an opaque
/// expression with a diagnostic, and the rest of the file must still be a tree.
/// </summary>
public sealed class JavaFrontendTests
{
    private const string Source = """
        package com.example.demo;

        import java.io.IOException;
        import java.util.List;

        /* JADX INFO: a comment JADX writes and the reader must skip */
        public class Hello {
            private static final String TAG = "Hello";
            private int count;

            public Hello(int count) {
                this.count = count;
            }

            public int add(int a, int b) {
                int sum = a + b;
                if (sum > 10) {
                    return 10;
                }
                return sum;
            }

            public void loop(List<String> items) {
                for (int i = 0; i < items.size(); i++) {
                    System.out.println(items.get(i));
                }
                for (String item : items) {
                    if (item != null && !item.isEmpty()) {
                        this.count++;
                    }
                }
            }

            public String describe(int value) {
                switch (value) {
                    case 1:
                        return "one";
                    case 2:
                    case 3:
                        return "few";
                    default:
                        return "many";
                }
            }

            public void read(java.io.InputStream input) throws Exception {
                try {
                    int x = input.read();
                } catch (java.io.IOException | RuntimeException e) {
                    throw e;
                } finally {
                    input.close();
                }
            }

            public Runnable background() {
                return new Runnable() {
                    @Override
                    public void run() {
                        System.out.println("done");
                    }
                };
            }
        }
        """;

    [Fact]
    public void A_jadx_file_becomes_one_type_with_its_members()
    {
        var document = new JavaFrontend().Translate(Source, "Hello.java");

        Assert.Equal(MetacodeLanguage.Java, document.Language);
        Assert.Equal("com.example.demo", Assert.Single(document.Modules).Name);

        var type = Assert.IsType<MetacodeTypeDeclaration>(Assert.Single(document.Modules[0].Declarations));
        Assert.Equal("Hello", type.Name);
        Assert.Equal(MetacodeTypeKind.Class, type.Kind);
        Assert.True(type.Modifiers.HasFlag(MetacodeModifiers.Public));

        var field = Assert.Single(type.Members.OfType<MetacodeField>(), member => member.Name == "TAG");
        Assert.Equal("String", field.Type!.Name);
        Assert.True(field.Modifiers.HasFlag(MetacodeModifiers.ReadOnly | MetacodeModifiers.Static));
        Assert.Equal("Hello", Assert.IsType<MetacodeLiteral>(field.Initializer).Text.Trim('"'));

        var constructor = Assert.Single(type.Members.OfType<MetacodeFunction>(), m => m.Kind == MetacodeFunctionKind.Constructor);
        Assert.Equal("Hello", constructor.Name);
        Assert.Single(constructor.ParameterList);
        Assert.NotNull(constructor.Body);

        var add = Assert.Single(type.Members.OfType<MetacodeFunction>(), m => m.Name == "add");
        Assert.Equal(MetacodeFunctionKind.Method, add.Kind);
        Assert.Equal("int", add.ReturnType!.Name);
        Assert.Equal(2, add.ParameterList.Count);

        var @if = Assert.IsType<MetacodeIf>(Assert.Single(add.Body!.Statements.OfType<MetacodeIf>()));
        Assert.Single(@if.Then.Statements.OfType<MetacodeReturn>());
        Assert.Single(add.Body.Statements.OfType<MetacodeReturn>());
    }

    [Fact]
    public void The_control_flow_jadx_emits_is_placed_in_the_model()
    {
        var document = new JavaFrontend().Translate(Source, "Hello.java");
        var type = (MetacodeTypeDeclaration)document.Modules[0].Declarations[0];

        // No construct in this file should be a parse failure: only the anonymous class is reported,
        // because the model has no node for a class body inside an expression.
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Code == MetacodeDiagnosticCodes.ParseError);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedExpression);

        var loop = Assert.Single(type.Members.OfType<MetacodeFunction>(), m => m.Name == "loop");
        Assert.Contains(loop.Body!.Statements, statement => statement is MetacodeFor);
        Assert.Contains(loop.Body.Statements, statement => statement is MetacodeForEach);

        var forEach = loop.Body.Statements.OfType<MetacodeForEach>().Single();
        Assert.Equal("item", forEach.VariableName);
        Assert.Equal("String", forEach.VariableType!.Name);
        Assert.Contains(forEach.Body.Statements, statement => statement is MetacodeIf);

        var describe = Assert.Single(type.Members.OfType<MetacodeFunction>(), m => m.Name == "describe");
        var @switch = Assert.IsType<MetacodeSwitch>(describe.Body!.Statements[0]);
        Assert.Equal(4, @switch.Cases.Count);
        Assert.True(@switch.Cases[^1].IsDefault);
        // `case 2:` falls through to `case 3:`: an empty body is the fall-through, not a missing case.
        Assert.Empty(@switch.Cases[1].Body.Statements);

        var read = Assert.Single(type.Members.OfType<MetacodeFunction>(), m => m.Name == "read");
        var @try = Assert.IsType<MetacodeTry>(read.Body!.Statements[0]);
        Assert.Single(@try.Catches);
        Assert.Equal("java.io.IOException | RuntimeException", @try.Catches[0].ExceptionType!.Render());
        Assert.NotNull(@try.Finally);

        var background = Assert.Single(type.Members.OfType<MetacodeFunction>(), m => m.Name == "background");
        var @return = Assert.IsType<MetacodeReturn>(background.Body!.Statements[0]);
        Assert.IsType<MetacodeOpaqueExpression>(@return.Value);
    }

    [Fact]
    public void The_canonical_text_reads_like_the_other_languages()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromJava(Source, "Hello.java"));
        var text = new PseudocodeRenderer().Render(document);

        Assert.Contains("MODULE com.example.demo", text);
        Assert.Contains("CLASS Hello", text);
        Assert.Contains("METHOD add", text);
        Assert.Contains("CONSTRUCTOR Hello", text);
        Assert.Contains("IF ", text);
        Assert.Contains("SWITCH ", text);
        Assert.Contains("CATCH (java.io.IOException | RuntimeException e)", text);
        Assert.Contains("RAW[java]", text);          // the anonymous class, kept and labelled
    }

    [Fact]
    public void An_interface_and_an_enum_are_read_too()
    {
        const string source = """
            package demo;

            public interface Greeter {
                String greet(String name);
                default String hello() { return greet("world"); }
                int LIMIT = 3;
            }

            enum Mode {
                ON, OFF(0), AUTO(1);

                private final int code;
                Mode(int code) { this.code = code; }
                Mode() { this(1); }
            }
            """;

        var document = new JavaFrontend().Translate(source, "Demo.java");

        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Code == MetacodeDiagnosticCodes.ParseError);

        var declarations = document.Modules[0].Declarations;
        var greeter = Assert.IsType<MetacodeTypeDeclaration>(declarations[0]);
        Assert.Equal(MetacodeTypeKind.Interface, greeter.Kind);
        Assert.Contains(greeter.Members.OfType<MetacodeFunction>(), m => m.Name == "greet" && m.Body is null);
        Assert.Contains(greeter.Members.OfType<MetacodeFunction>(), m => m.Name == "hello" && m.Body is not null);
        Assert.Contains(greeter.Members.OfType<MetacodeField>(), f => f.Name == "LIMIT");

        var mode = Assert.IsType<MetacodeTypeDeclaration>(declarations[1]);
        Assert.Equal(MetacodeTypeKind.Enum, mode.Kind);
        Assert.Equal(3, mode.Members.OfType<MetacodeEnumMember>().Count());
        Assert.Contains(mode.Members.OfType<MetacodeEnumMember>(), member => member.Value is MetacodeObjectCreation);
    }

    [Fact]
    public void A_source_the_reader_cannot_finish_keeps_the_statement_it_chokes_on()
    {
        const string source = """
            public class Broken {
                public void run() {
                    int ok = 1;
                    assert ok > 0;
                    int still = 2;
                }
            }
            """;

        var document = new JavaFrontend().Translate(source, "Broken.java");
        var type = (MetacodeTypeDeclaration)document.Modules[0].Declarations[0];
        var method = type.Members.OfType<MetacodeFunction>().Single();

        // `assert` has no node, so it is kept opaque; the declarations after it are still read — one
        // awkward statement costs that statement, not the method.
        Assert.Contains(method.Body!.Statements, statement => statement is MetacodeOpaqueStatement);
        Assert.Contains(method.Body.Statements, statement =>
            statement is MetacodeLocalVariable variable && variable.Name == "still");
    }
}
