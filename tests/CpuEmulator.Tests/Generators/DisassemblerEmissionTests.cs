using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Generators;

public class DisassemblerEmissionTests
{
    // The mnemonic whitelist (CPUGEN004: 1-8 uppercase alnum) guarantees all mnemonics in the
    // generated table are safe for direct string interpolation — no escaping needed in the
    // Disassemble switch arms.

    [Theory]
    [InlineData(0xA9, 0x42, 0x00, "LDA #$42")]
    [InlineData(0xA5, 0x10, 0x00, "LDA $10")]
    [InlineData(0xAD, 0x34, 0x12, "LDA $1234")]
    [InlineData(0x8D, 0x34, 0x12, "STA $1234")]
    [InlineData(0xAA, 0x00, 0x00, "TAX")]
    [InlineData(0x4C, 0x00, 0x80, "JMP $8000")]
    [InlineData(0xD0, 0xFC, 0x00, "BNE *-4")]
    [InlineData(0xD0, 0x05, 0x00, "BNE *+5")]
    [InlineData(0xFF, 0x00, 0x00, "???")]
    public void Disassemble_formats_by_addressing_mode(byte opcode, byte lo, byte hi, string expected) =>
        Assert.Equal(expected, Mos6502Cpu.Disassemble(opcode, lo, hi));

    [Fact]
    public void Generated_text_contains_Disassemble_method_signature()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Contains(
            "public static string Disassemble(byte opcode, byte operandLo, byte operandHi)",
            result.GeneratedText);
    }
}
