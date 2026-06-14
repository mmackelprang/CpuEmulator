using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class Z80DdFdSemanticsTests
{
    [Theory]
    // Indexed memory forms (plane-agnostic op text — the prefix selects IX/IY in the emit arm).
    [InlineData(0x7E, "LD",  "Indexed", false, "[DdFdLdIndexed(\"LOAD\",\"A\")]")]
    [InlineData(0x46, "LD",  "Indexed", false, "[DdFdLdIndexed(\"LOAD\",\"B\")]")]
    [InlineData(0x70, "LD",  "Indexed", false, "[DdFdLdIndexed(\"STORE\",\"B\")]")]
    [InlineData(0x77, "LD",  "Indexed", false, "[DdFdLdIndexed(\"STORE\",\"A\")]")]
    [InlineData(0x36, "LD",  "Indexed", false, "[DdFdStoreImmIndexed()]")]
    [InlineData(0x86, "ADD", "Indexed", false, "[DdFdAluIndexed(\"ADD\")]")]
    [InlineData(0xBE, "CP",  "Indexed", false, "[DdFdAluIndexed(\"CP\")]")]
    [InlineData(0x34, "INC", "Indexed", false, "[DdFdIncDecIndexed(false)]")]
    [InlineData(0x35, "DEC", "Indexed", false, "[DdFdIncDecIndexed(true)]")]
    // IX 16-bit (DD) / IY (FD).
    [InlineData(0x09, "ADD", "Register", false, "[Add16(\"IX\",\"BC\")]")]
    [InlineData(0x29, "ADD", "Register", false, "[Add16(\"IX\",\"IX\")]")]
    [InlineData(0x21, "LD",  "ImmediateExtended", false, "[Load16(\"IX\")]")]
    [InlineData(0x22, "LD",  "ExtendedAddress", false, "[Store16(\"IX\")]")]
    [InlineData(0x2A, "LD",  "ExtendedAddress", false, "[LoadMem16(\"IX\")]")]
    [InlineData(0x23, "INC", "Register", false, "[Inc16(\"IX\")]")]
    [InlineData(0x2B, "DEC", "Register", false, "[Dec16(\"IX\")]")]
    [InlineData(0xE5, "PUSH", "Register", false, "[Push16(\"IX\")]")]
    [InlineData(0xE1, "POP",  "Register", false, "[Pop16(\"IX\")]")]
    [InlineData(0xE3, "EX",  "RegisterIndirect", false, "[ExSpHl()]")]
    [InlineData(0xE9, "JP",  "RegisterIndirect", false, "[JumpIndirect()]")]
    [InlineData(0xF9, "LD",  "Register", false, "[Transfer(\"IX\",\"SP\")]")]
    [InlineData(0x21, "LD",  "ImmediateExtended", true,  "[Load16(\"IY\")]")]
    [InlineData(0x29, "ADD", "Register", true,  "[Add16(\"IY\",\"IY\")]")]
    // Undoc half (DD -> IXh/IXl ; FD -> IYh/IYl). The ALU half forms (DD 84) keep [Add8()] — the
    // source is resolved prefix-aware in the emit arm, NOT by text substitution.
    [InlineData(0x24, "INC", "Register", false, "[IncReg(\"IXh\")]")]
    [InlineData(0x2C, "INC", "Register", false, "[IncReg(\"IXl\")]")]
    [InlineData(0x25, "DEC", "Register", false, "[DecReg(\"IXh\")]")]
    [InlineData(0x26, "LD",  "Immediate", false, "[Load(\"IXh\")]")]
    [InlineData(0x7C, "LD",  "Register", false, "[Transfer(\"IXh\",\"A\")]")]
    [InlineData(0x60, "LD",  "Register", false, "[Transfer(\"B\",\"IXh\")]")]
    [InlineData(0x84, "ADD", "Register", false, "[Add8()]")]
    [InlineData(0x2C, "INC", "Register", true,  "[IncReg(\"IYl\")]")]
    // Inert prefix (DD on an op naming none of H/L/(HL)).
    [InlineData(0x04, "INC", "Register", false, "[IncReg(\"B\")]")]
    [InlineData(0x80, "ADD", "Register", false, "[Add8()]")]
    [InlineData(0x00, "NOP", "Implied",  false, "[]")]
    [InlineData(0xEB, "EX",  "Register", false, "[ExDeHl()]")]
    public void Derivation_produces_expected_ops(int op, string mn, string mode, bool isIy, string expected)
        => Assert.Equal(expected, Z80DdFdSemantics.OpsFor(op, mn, mode, isIy));

    [Fact]
    public void OpsFor_returns_null_for_the_prefix_bytes()
    {
        Assert.Null(Z80DdFdSemantics.OpsFor(0xCB, "?", "?", false));
        Assert.Null(Z80DdFdSemantics.OpsFor(0xDD, "?", "?", false));
        Assert.Null(Z80DdFdSemantics.OpsFor(0xED, "?", "?", false));
        Assert.Null(Z80DdFdSemantics.OpsFor(0xFD, "?", "?", false));
    }

    // The 252 DD + 252 FD core-row F1 cross-check (the M3.4c probe-vs-emitted discipline) lands with the
    // derived rows in Task 7 (Dataset_has_all_252_DD_and_252_FD_core_rows).
}
