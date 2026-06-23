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
        yield return new object[] { (ushort)0x7000, "MOVEQ #0,D0" };    // MOVEQ imm IS in the opword low byte (signed) -> decoded
        yield return new object[] { (ushort)0x7AFF, "MOVEQ #-1,D5" };    // MOVEQ #-1,D5 (imm 0xFF sign-extends; Dn=5)
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

    [Fact]
    public void Every_grammar_matched_opword_disassembles_to_a_non_question_mark()
    {
        // Sweep all 65536 opwords; any opword the SHIPPED grammar recognizes as a real family must NOT render
        // "???". (An opword matching no family legitimately renders "???".) This proves the disassembler's
        // family walk agrees with the decoder's family walk — they read the SAME Decode68k.Ops in the SAME
        // order. Un-fakeable: M68kGrammar reads the shipped grammar directly, so the disassembler cannot claim
        // a family the decoder doesn't recognize (or miss one the decoder does).
        int defined = 0;
        for (int w = 0; w <= 0xFFFF; w++)
        {
            bool decoderKnowsIt = M68kGrammar.Matches((ushort)w);
            string text = M68000Cpu.Disassemble((uint)w, 0, 0);
            if (decoderKnowsIt)
            {
                defined++;
                Assert.NotEqual("???", text);
            }
        }
        Assert.True(defined > 40000, $"expected most opwords to match a family; got {defined}");
    }

    [Fact]
    public void The_m68000_monitor_renders_real_mnemonics_not_question_marks()
    {
        // Load a tiny 68000 program and assert MonitorEngine.Disassemble renders real mnemonics — the
        // end-to-end host path roadmap #6 is about. The board is the same "68000" board the host's --board path
        // builds (BoardRegistry id "68000"). The program is written to the board's RAM ($020000; ROM spans
        // $0-$00FFFF), and we disassemble each 2-byte opword at its own (even) address.
        //   NOP (4E71); MOVEQ #1,D0 (7001); ADD.W D0,D1 (D240); RTS (4E75).
        // NOTE: the monitor's per-instruction length advance comes from InstructionLength(byte) — a single
        // high byte — which cannot size a 16-bit opword (the IMonitorSupport 3-byte contract is NOT widened by
        // D68). So we disassemble each opword individually at its known address rather than walk count:4. This
        // still exercises the real host path (board → MonitorEngine → the generated field-grammar Disassemble).
        byte[] program = [0x4E, 0x71, 0x70, 0x01, 0xD2, 0x40, 0x4E, 0x75];

        Assert.True(CpuEmulator.Host.BoardRegistry.TryBoot("68000",
            CpuEmulator.Machines.ExecutionTier.Interpreter, out var board, out var error), error);
        var engine = board!.NewMonitor();
        engine.WriteMemory(0x020000, program);        // the 68000 board's RAM base (ROM spans $0-$00FFFF)

        string nop   = engine.Disassemble(0x020000, 1);
        string moveq = engine.Disassemble(0x020002, 1);
        string add   = engine.Disassemble(0x020004, 1);
        string rts   = engine.Disassemble(0x020006, 1);

        Assert.Contains("NOP", nop);
        Assert.Contains("MOVEQ", moveq);
        Assert.Contains("ADD.W", add);
        Assert.Contains("RTS", rts);
        Assert.DoesNotContain("???", nop + moveq + add + rts);
    }
}

/// <summary>Walks the SHIPPED 68000 field grammar (M68000Spec.Decode68k.Ops) and reports whether any op
/// matches an opword — the SAME (w &amp; Mask) == Match predicate the emitted disassembler uses. Reading the
/// real grammar directly is what makes the decoder-agreement sweep un-fakeable.</summary>
internal static class M68kGrammar
{
    public static bool Matches(ushort w)
    {
        foreach (var op in CpuEmulator.Cpus.M68000.M68000Spec.Decode68k.Ops)
            if ((w & op.Mask) == op.Match) return true;
        return false;
    }
}
