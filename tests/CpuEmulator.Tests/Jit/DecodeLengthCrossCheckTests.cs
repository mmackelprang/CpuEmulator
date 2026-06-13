using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Tests.Generators;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 5 (Ground truth E + the four-notions-of-length convergence note): the load-bearing
/// cross-check that the walk's per-mode FixedLength agrees with InstructionLength AND with the
/// per-mode constant the interpreter bodies advance PC by. This pins notions (2) FixedLength,
/// (3) InstructionLength, and the per-mode ModeLength so the walk (JIT/monitor) and the per-body PC
/// advance (interpreter) cannot drift. (Notion (4), the importer ExpectedBytes, is pinned in Task 6.)</summary>
public class DecodeLengthCrossCheckTests
{
    // The per-mode byte length the interpreter bodies consume (the old ModeLength table, the
    // authoritative per-template PC advance — notion (1)).
    private static int ModeLength(JitMode mode) => mode switch
    {
        JitMode.Implied or JitMode.Accumulator => 1,
        JitMode.Immediate or JitMode.ZeroPage or JitMode.ZeroPageX or JitMode.ZeroPageY
            or JitMode.IndirectX or JitMode.IndirectY or JitMode.Relative => 2,
        JitMode.Absolute or JitMode.AbsoluteX or JitMode.AbsoluteY or JitMode.Indirect => 3,
        _ => throw new System.InvalidOperationException($"no length for mode {mode}"),
    };

    [Fact]
    public void Walk_FixedLength_matches_interpreter_body_PC_advance_for_every_opcode()
    {
        for (int op = 0; op <= 0xFF; op++)
        {
            OpcodeDescriptor d = Mos6502Cpu.DescriptorFor((byte)op);
            if (d.Class == JitOpClass.Undefined)
                continue;   // undefined opcodes are the sentinel (FixedLength 1); not a documented mode

            int expected = ModeLength(d.Mode);
            // notion (2): the descriptor's FixedLength is the per-mode constant.
            Assert.Equal(expected, d.FixedLength);
            // notion (3): InstructionLength (the monitor path) agrees.
            Assert.Equal(expected, Mos6502Cpu.InstructionLength((byte)op));
            // Every documented 6502 opcode is LengthRule.Fixed (no ModRmDetermined row).
            Assert.Equal(LengthRule.Fixed, d.LengthRule);
        }
    }

    // ── Byte-identical-region spot pins (Ground truth E "NO" rows) ───────────────────────────

    [Fact]
    public void Interpreter_body_for_LDA_is_unchanged()
    {
        // The per-op interpreter bodies do NOT move (Ground truth E): they still do their own
        // ReadBus(PC); PC++ and the per-template work. The walk computes the TOTAL the JIT/monitor
        // need; the bodies are byte-identical. Pin the LDA-Immediate (0xA9) body text.
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains(
            "    private void OpA9()\n    {\n        byte data = ReadBus(PC);\n"
          + "        PC = unchecked((ushort)(PC + 1));\n        A = data;\n",
            result.GeneratedText.Replace("\r\n", "\n"));
    }

    [Fact]
    public void TryAssemble_is_unchanged()
    {
        // The decode inverse (mnemonic -> bytes) is unchanged in direction: LDA #$42 -> A9 42.
        bool ok = Mos6502Cpu.TryAssemble("LDA", "#$42", out byte[] bytes, out string? error);

        Assert.True(ok, error);
        Assert.Equal(new byte[] { 0xA9, 0x42 }, bytes);
    }
}
