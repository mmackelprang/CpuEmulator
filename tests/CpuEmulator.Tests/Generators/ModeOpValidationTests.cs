namespace CpuEmulator.Tests.Generators;

public class ModeOpValidationTests
{
    // Replace helper matching InstructionParsingTests.WithInstructions pattern.
    private static string WithInstructions(string instructionsBody) =>
        GeneratorHappyPathTests.ValidSpecSource.Replace(
            """
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
                    Insn(0xEA, "NOP", AddrMode.Implied, []),
                ];
            """,
            instructionsBody);

    [Fact]
    public void Store_with_immediate_mode_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "STA", AddrMode.Immediate, [Store(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Jump_with_zero_page_mode_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "JMP", AddrMode.ZeroPage, [Jump()]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Branch_with_non_relative_mode_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "BNE", AddrMode.Absolute, [BranchIf(Flag.Z, false)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Relative_requires_exactly_one_branch_op()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "BNE", AddrMode.Relative, [BranchIf(Flag.Z, false), SetNZ(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Load_must_be_first_in_load_class()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "LDA", AddrMode.Absolute, [SetNZ(Reg.A), Load(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Implied_with_memory_op_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "STA", AddrMode.Implied, [Store(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Empty_ops_allowed_only_for_implied()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "XYZ", AddrMode.Absolute, []),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Invalid_mnemonic_is_rejected()
    {
        // Mnemonic contains a newline — not in [A-Z][A-Z0-9]{0,7}.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "BAD\nNAME", AddrMode.Implied, []),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN004");
    }

    [Fact]
    public void Unknown_flag_member_is_rejected()
    {
        // Flag.B is not in the valid whitelist {C,Z,I,D,V,N}.
        // Note: this also won't compile (Flag.B may not exist), but the generator
        // should emit CPUGEN006 first via its own flag-whitelist check.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "BNE", AddrMode.Relative, [BranchIf(Flag.B, false)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN006");
    }

    [Fact]
    public void Valid_subset_passes_with_no_CPUGEN_diagnostics()
    {
        // The full 11-opcode 6502 subset (from Mos6502Spec) run through the harness.
        // Uses ValidSpecSource registers (A, X, S, P, PC) — drops Y-dependent insns.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
                    Insn(0xA5, "LDA", AddrMode.ZeroPage,  [Load(Reg.A), SetNZ(Reg.A)]),
                    Insn(0xAD, "LDA", AddrMode.Absolute,  [Load(Reg.A), SetNZ(Reg.A)]),
                    Insn(0x85, "STA", AddrMode.ZeroPage,  [Store(Reg.A)]),
                    Insn(0x8D, "STA", AddrMode.Absolute,  [Store(Reg.A)]),
                    Insn(0xA2, "LDX", AddrMode.Immediate, [Load(Reg.X), SetNZ(Reg.X)]),
                    Insn(0xAA, "TAX", AddrMode.Implied,   [Transfer(Reg.A, Reg.X), SetNZ(Reg.X)]),
                    Insn(0xE8, "INX", AddrMode.Implied,   [Increment(Reg.X), SetNZ(Reg.X)]),
                    Insn(0x4C, "JMP", AddrMode.Absolute,  [Jump()]),
                    Insn(0xD0, "BNE", AddrMode.Relative,  [BranchIf(Flag.Z, false)]),
                    Insn(0xEA, "NOP", AddrMode.Implied,   []),
                ];
            """));

        Assert.Empty(result.GeneratorDiagnostics);
    }
}
