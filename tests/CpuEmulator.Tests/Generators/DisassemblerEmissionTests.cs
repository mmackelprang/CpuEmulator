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
    [InlineData(0xD0, 0x80, 0x00, "BNE *-128")] // sbyte min: negative section of +0;-0
    [InlineData(0xD0, 0x00, 0x00, "BNE *+0")]   // zero takes the POSITIVE section of +0;-0
    // Task 3: indexed zero-page and absolute modes
    [InlineData(0xB5, 0x10, 0x00, "LDA $10,X")]
    [InlineData(0xBD, 0x34, 0x12, "LDA $1234,X")]
    [InlineData(0xB9, 0x34, 0x12, "LDA $1234,Y")]
    [InlineData(0xB6, 0x10, 0x00, "LDX $10,Y")]
    // Task 4: indirect modes
    [InlineData(0xA1, 0x20, 0x00, "LDA ($20,X)")]
    [InlineData(0xB1, 0x20, 0x00, "LDA ($20),Y")]
    [InlineData(0x6C, 0x20, 0x03, "JMP ($0320)")]
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
