using CpuEmulator.Core.Jit;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kFieldDecodeWalkTests
{
    // A synthetic field-grammar CPU: ONE op "ADD" (mask 0xF100, match 0xD000), size in bits 7-6 (standard),
    // EA 6 bits in 5-0. The walk fetches a BIG-ENDIAN operword and packs the opaque (operation, size) key.
    // Shared with the M4.3b EA tests (M68kEaTestSpecs) — that spec is a strict superset (A0-A7 + D0-D7 vs the
    // original D0/A0), so these M4.3a assertions (key shape, undefined sentinel, length-per-EA-mode) hold
    // identically while the emitted M4.3b ComputeEa/Areg helpers (which name A0..A7) compile.
    private const string Source = M68kEaTestSpecs.AddGrammarCpu;

    [Fact]
    public void Operword_decodes_to_the_opaque_operation_size_key()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // ADD.w D0,D1-ish operword: 1101 001 0 01 000000 = 0xD240 (op match 0xD000, size 01=.w, EA = Dn 000:000).
        // Big-endian stream: high byte 0xD2, low byte 0x40.
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(
            new byte[] { 0xD2, 0x40 }, unitBytes: 2, bigEndian: true);
        var decode = t.GetMethod("Decode")!;
        dynamic r = decode.Invoke(null, new object[] { stream })!;
        // The opaque key packs (operationIndex, size). The exact packing is the generator's choice; the
        // test asserts it is STABLE + distinct per (op, size). Assert the key for ADD.w (size index 1).
        uint key = (uint)r.OperationKey;
        Assert.NotEqual(0xFFFFFFFFu, key);           // matched (not the illegal Undefined sentinel)
        Assert.NotEqual(0u, key);
        // The size .w is reflected in the key; ADD.b (size 00) packs a DIFFERENT key. Same op match +
        // same EA (Dn 000:000); ONLY the size field (bits 7-6) differs: 0xD240 = .w (01), 0xD200 = .b (00).
        var streamB = new CpuEmulator.Core.Jit.BufferFetchStream(
            new byte[] { 0xD2, 0x00 }, unitBytes: 2, bigEndian: true);   // 0xD200 → size 00 = .b
        dynamic rb = decode.Invoke(null, new object[] { streamB })!;
        Assert.NotEqual(key, (uint)rb.OperationKey);  // (ADD,.w) != (ADD,.b) — size is part of the key
    }

    [Fact]
    public void An_unmatched_operword_returns_the_undefined_sentinel()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // 0x0000 matches no field op (mask 0xF100 & 0x0000 = 0x0000 != match 0xD000).
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(
            new byte[] { 0x00, 0x00 }, unitBytes: 2, bigEndian: true);
        var decode = t.GetMethod("Decode")!;
        dynamic r = decode.Invoke(null, new object[] { stream })!;
        // DescriptorFor(key) yields the Undefined sentinel for an unmatched key (the illegal path; M4.5
        // vectors it). The sentinel carries JitOpClass.Undefined (the OpcodeDescriptor.Undefined accessor;
        // there is no IsUndefined property — Task 0 recon: DecodeLengthCrossCheckTests checks .Class).
        var descFor = t.GetMethod("DescriptorFor")!;
        dynamic d = descFor.Invoke(null, new object[] { (uint)r.OperationKey })!;
        Assert.Equal(JitOpClass.Undefined, (JitOpClass)d.Class);
    }

    [Theory]
    // ea (mode:reg)         size      expected total bytes (operword=2 + extWords×2)
    [InlineData(0x00, /*Dn  */ 1, 2)]   // Dn          : 0 ext words → 2
    [InlineData(0x10, /*(A0)*/ 1, 2)]   // (An)        : 0 ext words → 2
    [InlineData(0x28, /*d16 */ 1, 4)]   // d16(An)     : 1 ext word  → 4   (mode 5)
    [InlineData(0x38, /*absW*/ 1, 4)]   // abs.w       : 1 ext word  → 4   (mode 7, reg 0)
    [InlineData(0x39, /*absL*/ 1, 6)]   // abs.l       : 2 ext words → 6   (mode 7, reg 1)
    [InlineData(0x3C, /*#imm*/ 1, 4)]   // #imm.w      : 1 ext word  → 4   (mode 7, reg 4)
    [InlineData(0x3C, /*#imm*/ 2, 6)]   // #imm.l      : 2 ext words → 6   (size .l — the size-dependence!)
    public void Computed_length_follows_ea_mode_and_size(int ea, int sizeIndex, int expectedBytes)
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // Build an ADD operword (match 0xD000) with the given size bits (7-6) and EA (5-0). Pad the buffer
        // with enough extension-word bytes that NextUnit never runs past the end.
        ushort sizeBits = sizeIndex == 1 ? (ushort)(1 << 6) : (ushort)(2 << 6);   // .w = 01, .l = 10
        ushort operword = (ushort)(0xD000 | sizeBits | (ea & 0x3F));
        var buf = new byte[] { (byte)(operword >> 8), (byte)operword, 0, 0, 0, 0, 0, 0 };  // BE operword + padding
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        dynamic r = t.GetMethod("Decode")!.Invoke(null, new object[] { stream })!;
        Assert.Equal(expectedBytes, (int)r.Length);
    }
}
