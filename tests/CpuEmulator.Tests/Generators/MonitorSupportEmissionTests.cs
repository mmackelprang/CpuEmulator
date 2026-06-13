namespace CpuEmulator.Tests.Generators;

/// <summary>
/// Pins that the generator emits the IMonitorSupport implementation members correctly.
/// Task 1: interface wiring, InstructionLength, InterruptPending partial-property seam.
/// Task 2: TryAssemble body, AssembleOpcode arms, TryParseOperand helper.
/// </summary>
public class MonitorSupportEmissionTests
{
    // ── Task 1 pins ────────────────────────────────────────────────────────────

    [Fact]
    public void Generated_class_implements_IMonitorSupport()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains(
            ": CpuEmulator.Core.ICpuCore, CpuEmulator.Core.IMonitorSupport",
            result.GeneratedText);
        Assert.Contains("public static int InstructionLength(byte opcode)", result.GeneratedText);
        Assert.Contains("public partial bool InterruptPending { get; }", result.GeneratedText);
        Assert.Contains(
            "string CpuEmulator.Core.IMonitorSupport.ProgramCounterName => \"PC\";",
            result.GeneratedText);
    }

    [Fact]
    public void Generated_length_table_maps_modes()
    {
        // M3.1b (authorized test-change row 5): InstructionLength's signature is UNCHANGED but its
        // body now routes through the ONE decode model — DescriptorFor(opcode).FixedLength — instead
        // of its own switch(opcode) => ModeLength. The per-mode length now lives in the JitDescriptors
        // rows as LengthRule.Fixed, <len>. Behavior is byte-identical (the runtime InstructionLength
        // values are pinned by Mos6502MonitorSupportTests + GeneratedDecodeWalkTests).
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        // The body routes through the walk's resolver, not a per-opcode switch.
        Assert.Contains("public static int InstructionLength(byte opcode)", result.GeneratedText);
        Assert.Contains("=> DescriptorFor(opcode).FixedLength;", result.GeneratedText);
        // The per-mode length lives in the descriptor rows: Immediate (LDA #imm) → 2, Implied (NOP) → 1.
        Assert.Contains("CpuEmulator.Core.Jit.LengthRule.Fixed, 2", result.GeneratedText);
        Assert.Contains("CpuEmulator.Core.Jit.LengthRule.Fixed, 1", result.GeneratedText);
    }

    [Fact]
    public void Seam_contract_header_names_InterruptPending()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains(
            "// public partial bool InterruptPending",
            result.GeneratedText);
    }

    // ── Task 2 pins ────────────────────────────────────────────────────────────

    [Fact]
    public void Generated_TryAssemble_contains_AssembleOpcode_arm()
    {
        // Build a spec that has LDA Immediate so we can pin the exact arm text.
        // ValidSpecSource already has 0xA9 LDA Immediate.
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains("public static bool TryAssemble(", result.GeneratedText);
        // AssembleOpcode arm for LDA Immediate
        Assert.Contains("(\"LDA\", \"Immediate\") => 0xA9,", result.GeneratedText);
        // Accumulator fallback mention
        Assert.Contains("AssembleOpcode(m, \"Accumulator\")", result.GeneratedText);
    }

    [Fact]
    public void Generated_TryParseOperand_helper_present()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains("private static bool TryParseOperand(", result.GeneratedText);
    }
}
