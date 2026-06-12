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

    // Helper for tests that need a Y register in the spec (Y-indexed modes).
    private static string WithInstructionsAndY(string instructionsBody) =>
        GeneratorTestHost.ReplaceSection(
            WithInstructions(instructionsBody),
            """new("A", 8),""",
            """new("A", 8), new("Y", 8),""");

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

    // ── Task 2: 13-mode acceptance + class/mode matrix tests ──────────────────────────────

    [Fact]
    public void Load_with_zero_page_x_is_accepted()
    {
        // X is in ValidSpecSource's Registers; ZeroPageX must be accepted for load class.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xB5, "LDA", AddrMode.ZeroPageX, [Load(Reg.A), SetNZ(Reg.A)]),
                ];
            """));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.AllErrors); // emitted body must also COMPILE
    }

    [Fact]
    public void Load_with_absolute_y_is_accepted()
    {
        var result = GeneratorTestHost.Run(WithInstructionsAndY("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xB9, "LDA", AddrMode.AbsoluteY, [Load(Reg.A), SetNZ(Reg.A)]),
                ];
            """));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.AllErrors); // emitted body must also COMPILE
    }

    [Fact]
    public void Store_with_indirect_y_is_accepted()
    {
        var result = GeneratorTestHost.Run(WithInstructionsAndY("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x91, "STA", AddrMode.IndirectY, [Store(Reg.A)]),
                ];
            """));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.AllErrors); // emitted body must also COMPILE (exercises WriteBus)
    }

    [Fact]
    public void Jump_with_indirect_is_accepted()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x6C, "JMP", AddrMode.Indirect, [Jump()]),
                ];
            """));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.AllErrors); // emitted body must also COMPILE
    }

    [Fact]
    public void Load_with_accumulator_mode_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "LDA", AddrMode.Accumulator, [Load(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Jump_with_indirect_x_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "JMP", AddrMode.IndirectX, [Jump()]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Branch_with_absolute_x_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "BNE", AddrMode.AbsoluteX, [BranchIf(Flag.Z, false)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Y_indexed_mode_without_Y_register_is_rejected()
    {
        // AbsoluteY without a Y register in the spec — CPUGEN010 mentioning 'Y'.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "LDA", AddrMode.AbsoluteY, [Load(Reg.A)]),
                ];
            """));

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("register named 'Y'", diag.GetMessage());
    }

    [Fact]
    public void X_indexed_mode_requires_X_register()
    {
        // ValidSpecSource has X — so remove it by building a custom spec without X.
        string source = GeneratorTestHost.ReplaceSection(
            WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xB5, "LDA", AddrMode.ZeroPageX, [Load(Reg.A), SetNZ(Reg.A)]),
                ];
            """),
            """new("X", 8),""",
            "");

        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    // ── Tasks 5–7: ALU / RMW / stack / flag / flow parser-validation tests ────────────────

    [Fact]
    public void Adc_with_trailing_SetNZ_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "ADC", AddrMode.Immediate, [Adc(), SetNZ(Reg.A)]),
                ];
            """));

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("alu class must contain exactly one op", diag.GetMessage());
    }

    [Fact]
    public void Alu_with_implied_mode_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "ADC", AddrMode.Implied, [Adc()]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Compare_with_flag_argument_reports_CPUGEN011()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "CMP", AddrMode.Immediate, [Compare(Flag.C)]),
                ];
            """));

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN011");
        Assert.Contains("must be a Reg member", diagnostic.GetMessage());
    }

    [Fact]
    public void Adc_immediate_is_accepted()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x69, "ADC", AddrMode.Immediate, [Adc()]),
                ];
            """));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.AllErrors); // emitted body must also COMPILE
    }

    [Fact]
    public void Alu_without_status_register_is_rejected()
    {
        // ADC writes C/V/N/Z — a spec without a Status-role register cannot host it.
        string source = GeneratorTestHost.ReplaceSection(
            WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x69, "ADC", AddrMode.Immediate, [Adc()]),
                ];
            """),
            """new("P", 8, RegisterRole.Status),""",
            "");

        var result = GeneratorTestHost.Run(source);

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("Status", diag.GetMessage());
    }

    [Fact]
    public void Rmw_with_immediate_mode_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "ASL", AddrMode.Immediate, [ShiftLeft()]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Rmw_with_trailing_op_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "ASL", AddrMode.ZeroPage, [ShiftLeft(), SetNZ(Reg.A)]),
                ];
            """));

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("rmw class must contain exactly one op", diag.GetMessage());
    }

    [Fact]
    public void Asl_accumulator_is_accepted()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x0A, "ASL", AddrMode.Accumulator, [ShiftLeft()]),
                ];
            """));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.AllErrors); // emitted body must also COMPILE
    }

    [Fact]
    public void Push_with_absolute_mode_is_rejected()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "PHA", AddrMode.Absolute, [Push(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Jsr_with_implied_mode_is_rejected()
    {
        // Per-op flow matrix: Jsr requires Absolute. Must be CPUGEN010 at PARSE time,
        // not an emitter crash (CS8785) at generation time.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "JSR", AddrMode.Implied, [Jsr()]),
                ];
            """));

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("Jsr requires Absolute mode", diag.GetMessage());
    }

    [Fact]
    public void Rts_with_absolute_mode_is_rejected()
    {
        // Per-op flow matrix: Rts requires Implied. Must be CPUGEN010 at PARSE time.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "RTS", AddrMode.Absolute, [Rts()]),
                ];
            """));

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("Rts requires Implied mode", diag.GetMessage());
    }

    [Fact]
    public void Stack_op_without_stack_pointer_register_is_rejected()
    {
        // PHA touches the stack — a spec without a StackPointer-role register cannot host it.
        string source = GeneratorTestHost.ReplaceSection(
            WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x48, "PHA", AddrMode.Implied, [Push(Reg.A)]),
                ];
            """),
            """new("S", 8, RegisterRole.StackPointer),""",
            "");

        var result = GeneratorTestHost.Run(source);

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("StackPointer", diag.GetMessage());
    }

    [Fact]
    public void Pull_with_trailing_SetNZ_is_rejected()
    {
        // NZ is baked into Pull — the stack class allows no trailing ops.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "PLA", AddrMode.Implied, [Pull(Reg.A), SetNZ(Reg.A)]),
                ];
            """));

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("baked into Pull", diag.GetMessage());
    }

    [Fact]
    public void Clc_setflag_is_accepted_as_register_class()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x18, "CLC", AddrMode.Implied, [SetFlag(Flag.C, false)]),
                ];
            """));

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.AllErrors); // emitted body must also COMPILE
    }

    // ── Task 1: Reg-hardening tests ────────────────────────────────────────────────────────

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

    // ── Task 8 / 3b-ii: BRK/RTI flow-class acceptance + mode matrix ────────────────────────

    [Fact]
    public void Brk_implied_is_accepted()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x00, "BRK", AddrMode.Implied, [Brk()]),
                ];
            """));

        Assert.Empty(result.AllErrors);
    }

    [Fact]
    public void Rti_implied_is_accepted()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x40, "RTI", AddrMode.Implied, [Rti()]),
                ];
            """));

        Assert.Empty(result.AllErrors);
    }

    [Fact]
    public void Brk_with_absolute_mode_is_rejected()
    {
        // Per-op flow matrix: Brk requires Implied. Must be CPUGEN010 at PARSE time.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x99, "BRK", AddrMode.Absolute, [Brk()]),
                ];
            """));

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("Brk requires Implied mode", diag.GetMessage());
    }

    [Fact]
    public void Brk_with_trailing_op_is_rejected()
    {
        // Flow class is single-op: BRK's whole sequence is one fixed template.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x00, "BRK", AddrMode.Implied, [Brk(), SetNZ(Reg.A)]),
                ];
            """));

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("flow class must contain exactly one op", diag.GetMessage());
    }

    [Fact]
    public void Brk_without_status_register_is_rejected()
    {
        // BRK stacks P|0x30 and sets I — the emitter writes the Status register's NAME
        // into the template, so a spec without a Status-role register cannot host it.
        string source = GeneratorTestHost.ReplaceSection(
            WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0x00, "BRK", AddrMode.Implied, [Brk()]),
                ];
            """),
            """new("P", 8, RegisterRole.Status),""",
            "");

        var result = GeneratorTestHost.Run(source);

        var diag = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
        Assert.Contains("flow op 'Brk' requires a Status-role register", diag.GetMessage());
    }
}
