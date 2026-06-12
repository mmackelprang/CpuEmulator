namespace CpuEmulator.Tests.Generators;

public class ModeOpValidationTests
{
    // Replace helper matching InstructionParsingTests.WithInstructions pattern
    // (line-ending-agnostic + loud-failure guard via ReplaceSection).
    private static string WithInstructions(string instructionsBody) =>
        GeneratorTestHost.ReplaceSection(
            GeneratorHappyPathTests.ValidSpecSource,
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
    public void SetNZ_with_flag_argument_reports_CPUGEN011()
    {
        string source = WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "XYZ", AddrMode.Implied, [SetNZ(Flag.Z)]),
                ];
            """);
        var result = GeneratorTestHost.Run(source);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN011");
        Assert.Contains("must be a Reg member", diagnostic.GetMessage());
    }

    [Fact]
    public void BranchIf_with_register_first_argument_reports_CPUGEN011_at_that_argument()
    {
        string source = WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "BNE", AddrMode.Relative, [BranchIf(Reg.A, false)]),
                ];
            """);
        var result = GeneratorTestHost.Run(source);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN011");
        string locationText = source.Substring(
            diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);
        Assert.Equal("Reg.A", locationText); // points at the FIRST argument
        Assert.Contains("Argument 1 of 'BranchIf' must be a Flag member", diagnostic.GetMessage());
    }

    [Fact]
    public void Transfer_with_flag_second_argument_reports_CPUGEN011_at_that_argument()
    {
        string source = WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "TAX", AddrMode.Implied, [Transfer(Reg.A, Flag.C)]),
                ];
            """);
        var result = GeneratorTestHost.Run(source);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN011");
        string locationText = source.Substring(
            diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);
        Assert.Equal("Flag.C", locationText); // points at the SECOND argument
        Assert.Contains("Argument 2 of 'Transfer' must be a Reg member", diagnostic.GetMessage());
    }

    [Fact]
    public void Unknown_Reg_member_in_op_reports_CPUGEN011()
    {
        // Reg.Q is not in the Reg enum whitelist. Previously flowed silently to the emitter
        // and only failed as CS0103 in generated code — this test closes that hole.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "LDQ", AddrMode.Immediate, [Load(Reg.Q), SetNZ(Reg.Q)]),
                ];
            """));

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN011");
        Assert.Contains("must be a Reg member", diagnostic.GetMessage());
    }

    [Fact]
    public void Declared_register_not_in_Reg_enum_is_still_CPUGEN008_when_undeclared()
    {
        // Y is a valid Reg enum member but not declared in ValidSpecSource's Registers.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "LDY", AddrMode.Immediate, [Load(Reg.Y), SetNZ(Reg.Y)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN008" &&
            d.GetMessage().Contains("Y"));
    }

    [Fact]
    public void Valid_subset_passes_with_no_CPUGEN_diagnostics()
    {
        // The full 11-opcode 6502 subset, verbatim from Mos6502Spec, run through the
        // harness. ValidSpecSource's registers (A, X, S, P, PC) suffice: no instruction
        // in the subset references Y.
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
