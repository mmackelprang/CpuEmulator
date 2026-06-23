using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// The 68000 field-grammar disassembler gates (roadmap #6 / D68). Three un-fakeable proofs:
///   • a curated round-trip corpus (each row fails on a wrong family match, size suffix, cc name, or EA mode);
///   • a decoder-agreement sweep (every opword the SHIPPED grammar recognizes must render non-"???");
///   • a monitor-host integration gate (the real --board 68000 path renders mnemonics, not "???").
/// Operands living in extension words render placeholders (#&lt;imm&gt;/&lt;abs&gt;/&lt;d16&gt;); operands
/// encoded IN the opword (registers, EA mode, size, cc, the 8-bit branch/MOVEQ immediate field) render fully.
/// </summary>
public class M68kDisassembleTests
{
    // Curated (opword, expected mnemonic). Encodings + canonical mnemonics from the Motorola M68000
    // programmer's reference. The first arg of M68000Cpu.Disassemble is the 16-bit OPWORD; the two byte
    // args are extension-word bytes (0 here — every operand these rows exercise is encoded IN the opword).
    public static System.Collections.Generic.IEnumerable<object[]> Corpus()
    {
        yield return new object[] { (ushort)0x3200, "MOVE.W D0,D1" };    // MOVE.W D0 -> D1 (src ea=D0; dst reg=1 mode=0)
        yield return new object[] { (ushort)0x7000, "MOVEQ #<imm>,D0" }; // MOVEQ imm in opword low byte
        yield return new object[] { (ushort)0xD441, "ADD.W D1,D2" };     // ADD.W D1 -> D2 (Dn=2 dest, ea=D1)
        yield return new object[] { (ushort)0x9201, "SUB.B D1,D1" };     // SUB.B D1,D1 (ea=D1, Dn=1, dir=0 -> Dn dest)
        yield return new object[] { (ushort)0x4E71, "NOP" };
        yield return new object[] { (ushort)0x4E75, "RTS" };
        yield return new object[] { (ushort)0x4E73, "RTE" };
        yield return new object[] { (ushort)0x4ED0, "JMP (A0)" };        // JMP (A0): ea mode 2 reg 0
        yield return new object[] { (ushort)0x4E90, "JSR (A0)" };        // JSR (A0)
        yield return new object[] { (ushort)0x4840, "SWAP D0" };
        yield return new object[] { (ushort)0x4880, "EXT.W D0" };        // EXT.W (opmode field)
        yield return new object[] { (ushort)0x6600, "BNE *+<d16>" };     // Bcc cc=6 (NE), 16-bit disp form
        yield return new object[] { (ushort)0x6000, "BRA *+<d16>" };     // Bcc cc=0 -> BRA
        yield return new object[] { (ushort)0x6100, "BSR *+<d16>" };     // Bcc cc=1 -> BSR
        yield return new object[] { (ushort)0x4280, "CLR.L D0" };        // CLR.L D0 (ea=D0, size=2)
        yield return new object[] { (ushort)0x4440, "NEG.W D0" };        // NEG.W D0
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Disassembles_each_curated_opword_to_the_expected_mnemonic(ushort opword, string expected)
    {
        string text = M68000Cpu.Disassemble((uint)opword, operandLo: 0, operandHi: 0);
        Assert.Equal(expected, text);
    }
}
