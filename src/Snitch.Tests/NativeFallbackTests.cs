using Snitch.Languages.Native;

namespace Snitch.Tests;

/// <summary>
/// The native path is also reachable from the .NET tools: the PE check decides whether ILSpy or
/// Ghidra handles a file, and the Ghidra C is published as a real source artifact.
/// </summary>
[TestFixture]
public class NativeFallbackTests
{
    private static NativeSnapshot Sample() => new()
    {
        Image = "demo.exe",
        Language = "x86:LE:64:default",
        Entry = "140001000",
        ImageBase = "140000000",
        Functions =
        [
            new NativeFunction
            {
                Entry = "140001000",
                Name = "main",
                Signature = "int main(int argc, char ** argv)",
                Size = 42,
                Decompiled = "int main(int argc, char **argv) {\n  return 0;\n}",
            },
            new NativeFunction
            {
                Entry = "140002000",
                Name = "FUN_140002000",
                Signature = "void FUN_140002000(void)",
                Size = 8,
                Decompiled = "void FUN_140002000(void) { }",
            },
        ],
    };

    [Test]
    public void A_managed_assembly_is_recognised()
    {
        var managed = typeof(NativeFallbackTests).Assembly.Location;

        Assert.That(ManagedAssemblyDetector.IsManaged(managed), Is.True);
    }

    [Test]
    public void A_plain_file_is_not_a_managed_assembly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vmcp-native-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "not a PE file");
        try
        {
            Assert.That(ManagedAssemblyDetector.IsManaged(path), Is.False);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void A_missing_file_is_not_a_managed_assembly()
    {
        Assert.That(ManagedAssemblyDetector.IsManaged(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), Is.False);
    }

    [Test]
    public void FindFunction_matches_by_name_or_entry()
    {
        var snapshot = Sample();

        Assert.That(snapshot.FindFunction("main")?.Entry, Is.EqualTo("140001000"));
        Assert.That(snapshot.FindFunction("0x140002000")?.Name, Is.EqualTo("FUN_140002000"));
        Assert.That(snapshot.FindFunction("140002000")?.Name, Is.EqualTo("FUN_140002000"));
        Assert.That(snapshot.FindFunction("MAIN")?.Entry, Is.EqualTo("140001000"), "case-insensitive");
        Assert.That(snapshot.FindFunction("no-such-function"), Is.Null);
    }

    [Test]
    public void Source_writer_emits_one_block_per_function()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vmcp-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var destination = Path.Combine(dir, "NativeSource.c");
        try
        {
            NativeSourceWriter.Write(Sample(), @"C:\bin\demo.exe", "abc123", "12.2", destination);

            var text = File.ReadAllText(destination);
            Assert.That(text, Does.Contain("Ghidra 12.2"));
            Assert.That(text, Does.Contain("SHA-256  : abc123"));
            Assert.That(text, Does.Contain("===== 0x140001000  main"));
            Assert.That(text, Does.Contain("int main(int argc, char **argv)"));
            Assert.That(text, Does.Contain("===== 0x140002000  FUN_140002000"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
