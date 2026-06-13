using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Tests.Generators;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 3: the generated 6502 decode walk — Mos6502Cpu.Decode/DescriptorFor — replaces the
/// four switch(opcode) decode sites with ONE model. For the 6502 every row is LengthRule.Fixed and
/// key == opcode, so the walk is the degenerate case and BEHAVIOR is byte-identical (the unchanged
/// TomHarte/Klaus sweeps are the proof). These are the SHAPE pins.</summary>
public class GeneratedDecodeWalkTests
{
    [Fact]
    public void Generated_Decode_for_6502_NOP_returns_key_opcode_length_1()
    {
        // NOP (0xEA): the degenerate walk — Fixed, FixedLength == 1, key == opcode (Ground truth F row A).
        DecodeResult r = Mos6502Cpu.Decode(new BufferFetchStream(new byte[] { 0xEA }));

        Assert.Equal(0xEAu, r.OperationKey);
        Assert.Equal(1, r.Length);
        Assert.Equal(0, r.Operands.Count);
    }

    [Fact]
    public void Generated_Decode_for_LDA_immediate_returns_length_2()
    {
        // LDA #$42 (0xA9): key == opcode, COMPUTED length 2, the one operand byte the mode consumes.
        DecodeResult r = Mos6502Cpu.Decode(new BufferFetchStream(new byte[] { 0xA9, 0x42 }));

        Assert.Equal(0xA9u, r.OperationKey);
        Assert.Equal(2, r.Length);
        Assert.Equal(0x42, r.Operands.Lo);
        Assert.Equal(1, r.Operands.Count);
    }

    [Fact]
    public void Generated_Decode_for_LDA_absolute_returns_length_3_with_lo_hi()
    {
        // LDA $1234 (0xAD): COMPUTED length 3; the two operand bytes (lo then hi) the mode consumes.
        DecodeResult r = Mos6502Cpu.Decode(new BufferFetchStream(new byte[] { 0xAD, 0x34, 0x12 }));

        Assert.Equal(0xADu, r.OperationKey);
        Assert.Equal(3, r.Length);
        Assert.Equal(0x34, r.Operands.Lo);
        Assert.Equal(0x12, r.Operands.Hi);
        Assert.Equal(2, r.Operands.Count);
    }

    [Fact]
    public void DescriptorFor_6502_key_is_the_dense_array_index()
    {
        // Ground truth C: for the 6502 DescriptorFor(key) == JitDescriptors[(byte)key] — zero
        // hot-path regression; the consumers call DescriptorFor, never the raw table.
        Assert.Equal(Mos6502Cpu.JitDescriptors[0xA9], Mos6502Cpu.DescriptorFor(0xA9));
        Assert.Equal(Mos6502Cpu.JitDescriptors[0xEA], Mos6502Cpu.DescriptorFor(0xEA));
    }

    [Fact]
    public void InstructionLength_6502_routes_through_the_walk()
    {
        // Signature UNCHANGED (Ground truth E note); body routes through DescriptorFor(opcode).FixedLength.
        Assert.Equal(2, Mos6502Cpu.InstructionLength(0xA9));   // LDA Immediate
        Assert.Equal(1, Mos6502Cpu.InstructionLength(0xEA));   // NOP Implied
        Assert.Equal(3, Mos6502Cpu.InstructionLength(0xAD));   // LDA Absolute
    }

    [Fact]
    public void JitDescriptors_row_emits_LengthRule_Fixed_and_FixedLength()
    {
        // Generator-text spot (Ground truth E): the emitted descriptor rows carry
        // LengthRule.Fixed, FixedLength — not a bare positional length in the old Length slot.
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains("CpuEmulator.Core.Jit.LengthRule.Fixed, 2", result.GeneratedText);  // LDA Immediate row
        Assert.Contains("public static CpuEmulator.Core.Jit.DecodeResult Decode(", result.GeneratedText);
        Assert.Contains("public static CpuEmulator.Core.Jit.OpcodeDescriptor DescriptorFor(", result.GeneratedText);
    }
}
