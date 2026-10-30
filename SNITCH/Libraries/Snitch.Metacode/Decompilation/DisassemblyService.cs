using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// The IL view the decompiled workspace did not have: the bytes of each method body, decoded into
/// instructions with their tokens resolved against the metadata (a member reference prints its name, a
/// string token its value). Read-only, no execution, no Roslyn: a PE and its metadata.
/// </summary>
public interface IDisassemblyService
{
    object Disassemble(string assemblyPath, string? typeName, string? methodName, int maxMethods);
}

public sealed class DisassemblyService : IDisassemblyService
{
    private static readonly Dictionary<ushort, OpCode> OpCodes = BuildOpCodeTable();

    public object Disassemble(string assemblyPath, string? typeName = null, string? methodName = null,
        int maxMethods = 50)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return new { error = $"Assembly not found: {assemblyPath}" };
        if (!ManagedAssemblyDetector.IsManaged(assemblyPath))
            return new { error = $"{Path.GetFileName(assemblyPath)} is not a managed assembly." };

        maxMethods = Math.Clamp(maxMethods, 1, 500);

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var reader = new PEReader(stream);
            var metadata = reader.GetMetadataReader();

            var methods = new List<object>();
            var skipped = 0;
            var total = 0;

            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(typeHandle);
                var typeNamespace = metadata.GetString(type.Namespace);
                var typeSimple = metadata.GetString(type.Name);
                var fullType = string.IsNullOrEmpty(typeNamespace) ? typeSimple : typeNamespace + "." + typeSimple;

                if (!string.IsNullOrEmpty(typeName) &&
                    !fullType.Equals(typeName, StringComparison.OrdinalIgnoreCase) &&
                    !typeSimple.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var methodHandle in type.GetMethods())
                {
                    var method = metadata.GetMethodDefinition(methodHandle);
                    var name = metadata.GetString(method.Name);
                    if (!string.IsNullOrEmpty(methodName) && !name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    total++;
                    if (methods.Count >= maxMethods) continue;

                    if (method.RelativeVirtualAddress == 0)
                    {
                        skipped++;
                        continue;
                    }

                    string[] instructions;
                    try
                    {
                        var body = reader.GetMethodBody(method.RelativeVirtualAddress);
                        instructions = Decode(body, metadata);
                    }
                    catch (Exception ex)
                    {
                        instructions = ["// the method body could not be decoded: " + ex.Message];
                    }

                    methods.Add(new
                    {
                        type = fullType,
                        method = name,
                        instructions = instructions.Length,
                        il = instructions,
                    });
                }
            }

            return new
            {
                assembly = Path.GetFileName(assemblyPath),
                typeFilter = typeName,
                methodFilter = methodName,
                matched = total,
                returned = methods.Count,
                withoutBody = skipped,
                truncated = total > methods.Count + skipped,
                methods,
                note = "Token operands are resolved to names ('call string.Concat', 'ldstr \"…\"'); a MethodSpec " +
                       "prints as <spec>. The IL is the JIT's input, not the optimised machine code.",
            };
        }
        catch (Exception ex)
        {
            return new { error = $"The assembly could not be read: {ex.Message}", assemblyPath };
        }
    }

    // ── decoding ────────────────────────────────────────────────────────────
    private static string[] Decode(MethodBodyBlock body, MetadataReader metadata)
    {
        var lines = new List<string>();
        var il = body.GetILReader();
        var offset = 0;

        while (il.RemainingBytes > 0)
        {
            var start = offset;
            var code = il.ReadByte();
            offset++;

            OpCode opcode;
            if (code == 0xFE)
            {
                var second = il.ReadByte();
                offset++;
                if (!OpCodes.TryGetValue((ushort)(0xFE00 | second), out opcode))
                {
                    lines.Add($"IL_{start:X4}: <unknown two-byte opcode 0xFE{second:X2}>");
                    continue;
                }
            }
            else if (!OpCodes.TryGetValue(code, out opcode))
            {
                lines.Add($"IL_{start:X4}: <unknown opcode 0x{code:X2}>");
                continue;
            }

            var operand = Operand(opcode, il, metadata, ref offset);
            lines.Add(operand.Length == 0
                ? $"IL_{start:X4}: {opcode.Name}"
                : $"IL_{start:X4}: {opcode.Name} {operand}");
        }

        return lines.ToArray();
    }

    private static string Operand(OpCode opcode, BlobReader il, MetadataReader metadata, ref int offset)
    {
        switch (opcode.OperandType)
        {
            case OperandType.InlineNone:
                return string.Empty;

            case OperandType.ShortInlineI:
            {
                var value = il.ReadSByte();
                offset += 1;
                return value.ToString();
            }

            case OperandType.InlineI:
            {
                var value = il.ReadInt32();
                offset += 4;
                return value.ToString();
            }

            case OperandType.InlineI8:
            {
                var value = il.ReadInt64();
                offset += 8;
                return value.ToString();
            }

            case OperandType.ShortInlineR:
            {
                var value = il.ReadSingle();
                offset += 4;
                return value.ToString("R");
            }

            case OperandType.InlineR:
            {
                var value = il.ReadDouble();
                offset += 8;
                return value.ToString("R");
            }

            case OperandType.ShortInlineVar:
            {
                var value = il.ReadByte();
                offset += 1;
                return "V_" + value;
            }

            case OperandType.InlineVar:
            {
                var value = il.ReadUInt16();
                offset += 2;
                return "V_" + value;
            }

            case OperandType.ShortInlineBrTarget:
            {
                var delta = il.ReadSByte();
                offset += 1;
                return $"IL_{offset + delta:X4}";
            }

            case OperandType.InlineBrTarget:
            {
                var delta = il.ReadInt32();
                offset += 4;
                return $"IL_{offset + delta:X4}";
            }

            case OperandType.InlineSwitch:
            {
                var count = il.ReadInt32();
                offset += 4;
                var targets = new List<string>();
                var baseOffset = offset + count * 4;
                for (var index = 0; index < count; index++)
                {
                    var delta = il.ReadInt32();
                    targets.Add($"IL_{baseOffset + delta:X4}");
                }

                offset += count * 4;
                return "(" + string.Join(", ", targets) + ")";
            }

            case OperandType.InlineString:
            {
                var token = il.ReadInt32();
                offset += 4;
                try { return "\"" + metadata.GetUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF)) + "\""; }
                catch { return $"0x{token:X8}"; }
            }

            case OperandType.InlineMethod:
            case OperandType.InlineField:
            case OperandType.InlineType:
            case OperandType.InlineTok:
            {
                var token = il.ReadInt32();
                offset += 4;
                return ResolveToken(metadata, token);
            }

            case OperandType.InlineSig:
            {
                var token = il.ReadInt32();
                offset += 4;
                return $"sig 0x{token:X8}";
            }

            default:
            {
                var token = il.ReadInt32();
                offset += 4;
                return $"0x{token:X8}";
            }
        }
    }

    private static string ResolveToken(MetadataReader metadata, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        try
        {
            return handle.Kind switch
            {
                HandleKind.MemberReference => NameOf(metadata.GetMemberReference((MemberReferenceHandle)handle), metadata),
                HandleKind.MethodDefinition => metadata.GetString(metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
                HandleKind.FieldDefinition => metadata.GetString(metadata.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
                HandleKind.TypeReference => NameOf(metadata.GetTypeReference((TypeReferenceHandle)handle), metadata),
                HandleKind.TypeDefinition => NameOf(metadata.GetTypeDefinition((TypeDefinitionHandle)handle), metadata),
                HandleKind.MethodSpecification => "<method spec>",
                HandleKind.TypeSpecification => "<type spec>",
                _ => $"0x{token:X8}",
            };
        }
        catch
        {
            return $"0x{token:X8}";
        }
    }

    private static string NameOf(MemberReference reference, MetadataReader metadata)
    {
        var parent = reference.Parent;
        var owner = parent.Kind switch
        {
            HandleKind.TypeReference => NameOf(metadata.GetTypeReference((TypeReferenceHandle)parent), metadata),
            HandleKind.TypeDefinition => NameOf(metadata.GetTypeDefinition((TypeDefinitionHandle)parent), metadata),
            HandleKind.TypeSpecification => "<type spec>",
            _ => "?",
        };

        return owner + "::" + metadata.GetString(reference.Name);
    }

    private static string NameOf(TypeReference type, MetadataReader metadata)
    {
        var ns = metadata.GetString(type.Namespace);
        var name = metadata.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string NameOf(TypeDefinition type, MetadataReader metadata)
    {
        var ns = metadata.GetString(type.Namespace);
        var name = metadata.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static Dictionary<ushort, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<ushort, OpCode>();
        foreach (var field in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode) continue;
            table[unchecked((ushort)opcode.Value)] = opcode;
        }

        return table;
    }
}
