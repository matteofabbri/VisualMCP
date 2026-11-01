using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The Rust reader: a file with the pieces a real one has — <c>use</c> items, an attribute, a struct, an
/// enum with payloads, a trait with a provided method, an inherent impl, a trait impl, a match, a range
/// <c>for</c> and a macro call — lowered into the shared model. The reader parses the source itself, so
/// the test needs no toolchain: this is the whole point of <see cref="RustFrontend"/>
/// living in <see cref="MetacodeTranslator.Default"/>.
/// </summary>
public sealed class RustFrontendTests
{
    private const string Source = """
        use std::collections::HashMap;
        use std::fmt;

        /// A counter that remembers its steps.
        #[derive(Debug, Clone)]
        pub struct Counter {
            pub value: i32,
            label: String,
        }

        pub enum Step {
            Add(i32),
            Reset,
            Jump { by: i32 },
        }

        pub trait Advance {
            fn advance(&mut self, by: i32) -> i32;
            fn describe(&self) -> String {
                String::from("advance")
            }
        }

        impl Counter {
            pub fn new(label: &str) -> Self {
                Counter { value: 0, label: label.to_string() }
            }

            pub fn add(&mut self, by: i32) -> i32 {
                let mut total = self.value;
                for i in 0..by {
                    if i % 2 == 0 {
                        total += i;
                    }
                }
                self.value = total;
                total
            }
        }

        impl fmt::Display for Counter {
            fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                write!(f, "{}: {}", self.label, self.value)
            }
        }

        fn classify(value: i32) -> &'static str {
            match value {
                0 => "zero",
                n if n < 0 => "negative",
                _ => "positive",
            }
        }
        """;

    [Fact]
    public void A_rust_file_becomes_the_types_and_functions_it_declares()
    {
        var document = new RustFrontend().Translate(Source, "counter.rs");

        Assert.Equal(MetacodeLanguage.Rust, document.Language);
        Assert.Equal("counter", Assert.Single(document.Modules).Name);

        // `use` is how Rust names a dependency, not a declaration; the rest is one declaration each.
        var declarations = document.Modules[0].Declarations;
        Assert.Equal(6, declarations.Count);

        var counter = Assert.IsType<MetacodeTypeDeclaration>(declarations[0]);
        Assert.Equal("Counter", counter.Name);
        Assert.Equal(MetacodeTypeKind.Struct, counter.Kind);
        Assert.True(counter.Modifiers.HasFlag(MetacodeModifiers.Public));
        Assert.Equal(new[] { "derive(Debug, Clone)" }, counter.Decorators);

        var value = Assert.Single(counter.Members.OfType<MetacodeField>(), field => field.Name == "value");
        Assert.Equal("i32", value.Type!.Name);
        Assert.True(value.Modifiers.HasFlag(MetacodeModifiers.Public));
        Assert.Contains(counter.Members.OfType<MetacodeField>(), field => field.Name == "label" && field.Modifiers == MetacodeModifiers.None);

        var step = Assert.IsType<MetacodeTypeDeclaration>(declarations[1]);
        Assert.Equal(MetacodeTypeKind.Enum, step.Kind);
        Assert.Equal(3, step.Members.OfType<MetacodeEnumMember>().Count());
        Assert.Contains(step.Members.OfType<MetacodeEnumMember>(),
            member => member.Name == "Add" && member.Value is MetacodeCollection);
        Assert.Contains(step.Members.OfType<MetacodeEnumMember>(),
            member => member.Name == "Jump" && member.Value is MetacodeAnonymousObject);
        Assert.Contains(step.Members.OfType<MetacodeEnumMember>(), member => member.Name == "Reset" && member.Value is null);

        var advance = Assert.IsType<MetacodeTypeDeclaration>(declarations[2]);
        Assert.Equal(MetacodeTypeKind.Trait, advance.Kind);
        Assert.Contains(advance.Members.OfType<MetacodeFunction>(), function => function.Name == "advance" && function.Body is null);
        Assert.Contains(advance.Members.OfType<MetacodeFunction>(), function => function.Name == "describe" && function.Body is not null);

        var classify = Assert.Single(declarations.OfType<MetacodeFunction>(), function => function.Name == "classify");
        Assert.Equal(MetacodeFunctionKind.Function, classify.Kind);
        Assert.Equal("str", classify.ReturnType!.Name);
        Assert.True(classify.ReturnType.IsReference);
    }

    [Fact]
    public void An_impl_block_is_the_declaration_that_carries_the_types_members()
    {
        var document = new RustFrontend().Translate(Source, "counter.rs");
        var declarations = document.Modules[0].Declarations;

        var inherent = declarations.OfType<MetacodeTypeDeclaration>().First(type => type.Kind == MetacodeTypeKind.Class);
        Assert.Equal("Counter", inherent.Name);
        Assert.Null(inherent.BaseTypes);

        var newFunction = Assert.Single(inherent.Members.OfType<MetacodeFunction>(), function => function.Name == "new");
        Assert.Equal(MetacodeFunctionKind.Method, newFunction.Kind);
        Assert.Equal("Self", newFunction.ReturnType!.Name);
        Assert.Equal("Counter", Assert.IsType<MetacodeObjectCreation>(
            Assert.IsType<MetacodeReturn>(newFunction.Body!.Statements[^1]).Value).Type.Name);

        // `impl fmt::Display for Counter` says Counter extends the trait — the model's EXTENDS.
        var traitImpl = Assert.Single(declarations.OfType<MetacodeTypeDeclaration>(),
            type => type.Kind == MetacodeTypeKind.Class && type.BaseTypes is { Count: > 0 });
        Assert.Equal("Counter", traitImpl.Name);
        Assert.Equal("fmt::Display", Assert.Single(traitImpl.BaseTypes!).Name);

        // A trait impl's method is a method, and the macro call in its body keeps its name.
        var fmt = Assert.Single(traitImpl.Members.OfType<MetacodeFunction>());
        Assert.Equal("fmt", fmt.Name);
        Assert.Equal("fmt::Result", fmt.ReturnType!.Render());
        var write = Assert.IsType<MetacodeInvocation>(Assert.IsType<MetacodeReturn>(fmt.Body!.Statements[^1]).Value);
        Assert.Equal("write!", Assert.IsType<MetacodeIdentifier>(write.Callee).Name);
        Assert.Equal(4, write.ArgumentList.Count);
    }

    [Fact]
    public void The_control_flow_rust_uses_is_placed_in_the_model()
    {
        var document = new RustFrontend().Translate(Source, "counter.rs");

        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Code == MetacodeDiagnosticCodes.ParseError);

        var add = Assert.Single(
            document.Modules[0].Declarations.OfType<MetacodeTypeDeclaration>()
                .SelectMany(type => type.Members.OfType<MetacodeFunction>()),
            function => function.Name == "add");

        // `for i in 0..by` is a counted loop, the same node Python's range builds.
        var loop = Assert.IsType<MetacodeFor>(add.Body!.Statements.OfType<MetacodeFor>().Single());
        Assert.Equal("i", Assert.IsType<MetacodeLocalVariable>(loop.Initializer).Name);
        Assert.Equal("i < by", Render(loop.Condition!));
        Assert.True(add.Body.Statements.OfType<MetacodeReturn>().Any());

        var @if = Assert.IsType<MetacodeIf>(loop.Body.Statements[0]);
        Assert.Equal("i % 2 == 0", Render(@if.Condition));
        Assert.IsType<MetacodeAssignment>(Assert.Single(@if.Then.Statements));

        var classify = Assert.Single(
            document.Modules[0].Declarations.OfType<MetacodeFunction>(), function => function.Name == "classify");
        var @switch = Assert.IsType<MetacodeSwitch>(classify.Body!.Statements[0]);
        Assert.Equal(3, @switch.Cases.Count);
        Assert.True(@switch.Cases[^1].IsDefault);
        Assert.Equal("0", Assert.IsType<MetacodeLiteral>(@switch.Cases[0].Labels![0]).Text);
        // A guard is part of the pattern it guards, because the model's case has no guard slot.
        Assert.Equal("n if n < 0", Assert.IsType<MetacodePatternText>(@switch.Cases[1].Labels![0]).Pattern);
    }

    [Fact]
    public void The_canonical_text_reads_like_the_other_languages()
    {
        var text = new PseudocodeRenderer().Render(MetacodeTranslator.Default.Translate(MetacodeSource.FromRust(Source, "counter.rs")));

        Assert.Contains("MODULE counter", text);
        Assert.Contains("@derive(Debug, Clone)", text);
        Assert.Contains("PUBLIC STRUCT Counter", text);
        Assert.Contains("ENUM Step", text);
        Assert.Contains("TRAIT Advance", text);
        Assert.Contains("CLASS Counter EXTENDS fmt::Display", text);
        Assert.Contains("METHOD add(self : Self&, by : i32) : i32", text);
        // The range loop is a counted loop, so the normalization pass prints it as the C# `for` is printed.
        Assert.Contains("DECLARE i = 0", text);
        Assert.Contains("WHILE i < by DO", text);
        Assert.Contains("i += 1", text);
        Assert.Contains("SWITCH value", text);
        Assert.Contains("CASE 0", text);
        Assert.Contains("PATTERN (n if n < 0)", text);
        Assert.Contains("DEFAULT", text);
        Assert.Contains("write!(f, \"{}: {}\", self.label, self.value)", text);
    }

    [Fact]
    public void A_construct_with_no_node_is_kept_and_the_rest_still_lowers()
    {
        const string source = """
            macro_rules! square {
                ($x:expr) => { $x * $x };
            }

            fn read(path: &str) -> Result<String, std::io::Error> {
                let text = std::fs::read_to_string(path)?;
                let other = "still here";
                Ok(text + other)
            }
            """;

        var document = new RustFrontend().Translate(source, "io.rs");

        // The macro definition and the `?` operator have no node: each is reported and kept, and the
        // declarations after them are still read.
        Assert.Contains(document.Modules[0].Declarations, declaration => declaration is MetacodeOpaqueDeclaration);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedDeclaration);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedExpression);

        var read = Assert.Single(document.Modules[0].Declarations.OfType<MetacodeFunction>());
        Assert.Equal("read", read.Name);
        Assert.Equal(3, read.Body!.Statements.Count);
        Assert.IsType<MetacodeOpaqueExpression>(Assert.IsType<MetacodeLocalVariable>(read.Body.Statements[0]).Initializer);
        Assert.Equal("other", Assert.IsType<MetacodeLocalVariable>(read.Body.Statements[1]).Name);
    }

    [Fact]
    public void A_rust_path_is_read_by_the_default_pipeline()
    {
        const string source = """
            mod inner {
                pub fn twice(value: i32) -> i32 {
                    value * 2
                }
            }

            fn main() {
                let answer = inner::twice(21);
                println!("{}", answer);
            }
            """;

        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromText(source, MetacodeLanguage.Rust, "main.rs"));

        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Code == MetacodeDiagnosticCodes.NoFrontend);

        // A `mod` block is a module of its own, named by the path Rust would use to reach it.
        Assert.Equal(2, document.Modules.Count);
        Assert.Equal("main", document.Modules[0].Name);
        var inner = Assert.Single(document.Modules, module => module.Name == "main::inner");
        Assert.Single(inner.Declarations.OfType<MetacodeFunction>());
    }

    /// <summary>An expression as the canonical text prints it, through a one-line function body.</summary>
    private static string Render(MetacodeExpression expression)
    {
        var document = new MetacodeDocument(
            "x",
            MetacodeLanguage.Rust,
            new[]
            {
                new MetacodeModule("m", new MetacodeDeclaration[]
                {
                    new MetacodeFunction(
                        "f",
                        MetacodeFunctionKind.Function,
                        Body: new MetacodeBlock(new MetacodeStatement[] { new MetacodeReturn(expression) })),
                }),
            },
            Array.Empty<MetacodeDiagnostic>());

        var text = new PseudocodeRenderer().Render(document).Replace("\r", string.Empty);
        var line = text.Split('\n').Select(candidate => candidate.Trim())
            .First(candidate => candidate.StartsWith("RETURN", StringComparison.Ordinal));

        return line["RETURN ".Length..];
    }
}
