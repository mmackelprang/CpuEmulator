using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>The generated-artifact coherence property: the assembler is the exact inverse
/// of the disassembler, for every documented opcode, byte-for-byte. Both come from the same
/// spec table — a failure means the emitter's two tables drifted, the one bug class
/// artifact 5 exists to make impossible.</summary>
public class AssemblerRoundtripTests
{
    public static TheoryData<byte> ImplementedOpcodes()
    {
        var data = new TheoryData<byte>();   // same probe idiom as the TomHarte theory
        for (int opcode = 0; opcode <= 0xFF; opcode++)
            if (Mos6502Cpu.Disassemble((byte)opcode, 0, 0) != "???")
                data.Add((byte)opcode);
        return data;
    }

    [Theory]
    [MemberData(nameof(ImplementedOpcodes))]
    public void Assemble_inverts_Disassemble(byte opcode)
    {
        // (0x34, 0x12) exercises lo/hi ordering in 4-hex absolute text; (0xFC, 0x00)
        // exercises negative relative offsets. Implied/Accumulator ignore the operands.
        foreach ((byte lo, byte hi) in new[] { ((byte)0x34, (byte)0x12), ((byte)0xFC, (byte)0x00) })
        {
            string text = Mos6502Cpu.Disassemble(opcode, lo, hi);
            int space = text.IndexOf(' ');
            string mnemonic = space < 0 ? text : text.Substring(0, space);
            string operand = space < 0 ? string.Empty : text.Substring(space + 1);

            Assert.True(
                Mos6502Cpu.TryAssemble(mnemonic, operand, out byte[] bytes, out string? error),
                $"0x{opcode:X2} '{text}' failed to reassemble: {error}");

            byte[] expected = Mos6502Cpu.InstructionLength(opcode) switch
            {
                1 => new[] { opcode },
                2 => new[] { opcode, lo },
                _ => new[] { opcode, lo, hi },
            };
            Assert.Equal(expected, bytes);
        }
    }
}
