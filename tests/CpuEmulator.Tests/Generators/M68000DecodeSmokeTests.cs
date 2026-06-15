using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M4.4a decode smoke proof: the REAL importer-generated M68000Spec.cs populates Decode68k, so the
/// generated M68000Cpu.Decode(IFetchStream) resolves representative operwords to real (operation, size)
/// keys (not the illegal sentinel) with a word-granular computed length. NO op body exists yet — every
/// resolved key maps to the Undefined descriptor (op bodies + descriptor rows are M4.5). This pins the
/// honest close-state: real decode, no live semantics.
/// </summary>
public class M68000DecodeSmokeTests
{
    private const uint IllegalKey = 0xFFFFFFFFu;   // the field-decode walk's no-match sentinel (CpuEmitter)

    [Theory]
    // ADD.w D0,(A0): 1101 000 0 01 010 000 = 0xD050 (family ADD matches 0xF000/0xD000; size .w; EA = (An), mode 2).
    [InlineData(0xD0, 0x50)]
    // MOVE.w D0,D1: 0011 001 000 000 000 = 0x3200 (family MOVE matches 0xC000/0x0000; size .w via Move enc; EA Dn).
    [InlineData(0x32, 0x00)]
    // CLR.b D0: 0100 0010 00 000 000 = 0x4200 (family CLR).
    [InlineData(0x42, 0x00)]
    public void Representative_operwords_decode_to_a_real_key(byte hi, byte lo)
    {
        var stream = new BufferFetchStream(new byte[] { hi, lo, 0, 0, 0, 0 }, unitBytes: 2, bigEndian: true);
        DecodeResult r = M68000Cpu.Decode(stream);
        Assert.NotEqual(IllegalKey, r.OperationKey);   // a family matched (real decode)
        Assert.True(r.Length >= 2, $"length {r.Length} < 2 (must consume at least the operword)");
        Assert.Equal(0, r.Length % 2);                 // word-granular length
    }

    [Fact]
    public void ADD_w_and_ADD_b_resolve_to_distinct_keys()
    {
        // Same op (ADD, 0xF000/0xD000) + same EA (Dn), DIFFERENT size: .w (01 in bits 7-6) vs .b (00).
        // The (operation, size) key must differ — size is part of the key.
        var addW = new BufferFetchStream(new byte[] { 0xD0, 0x40, 0, 0 }, unitBytes: 2, bigEndian: true); // 0xD040 .w
        var addB = new BufferFetchStream(new byte[] { 0xD0, 0x00, 0, 0 }, unitBytes: 2, bigEndian: true); // 0xD000 .b
        uint keyW = M68000Cpu.Decode(addW).OperationKey;
        uint keyB = M68000Cpu.Decode(addB).OperationKey;
        Assert.NotEqual(IllegalKey, keyW);
        Assert.NotEqual(IllegalKey, keyB);
        Assert.NotEqual(keyW, keyB);
    }

    [Fact]
    public void An_unencoded_operword_is_the_illegal_sentinel()
    {
        // 0xFFFF is a guaranteed-illegal pattern (no family masks/matches it as a legal op).
        var stream = new BufferFetchStream(new byte[] { 0xFF, 0xFF, 0, 0 }, unitBytes: 2, bigEndian: true);
        DecodeResult r = M68000Cpu.Decode(stream);
        Assert.Equal(IllegalKey, r.OperationKey);
        Assert.Equal(2, r.Length);                     // the illegal op is one word
    }

    [Fact]
    public void Decode_resolves_to_an_Undefined_descriptor_in_m4_4a()
    {
        // M4.4a populates the FieldGrammar (decode) but NO descriptor rows (op bodies are M4.5):
        // every resolved key maps to the Undefined sentinel. This pins the honest close-state.
        // (OpcodeDescriptor has no IsUndefined member — the Undefined sentinel carries JitOpClass.Undefined,
        // matching how M68kFieldDecodeWalkTests checks the illegal path.)
        var stream = new BufferFetchStream(new byte[] { 0xD0, 0x50, 0, 0 }, unitBytes: 2, bigEndian: true);
        DecodeResult r = M68000Cpu.Decode(stream);
        OpcodeDescriptor d = M68000Cpu.DescriptorFor(r.OperationKey);
        Assert.Equal(JitOpClass.Undefined, d.Class);
    }
}
