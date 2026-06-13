namespace CpuEmulator.Tests.Generators;

/// <summary>M3.1a Task 6 — the abstraction proof. A synthetic "test CPU" whose register NAMES the
/// 6502 never had (BC/HL, 16-bit) generates, introspects, and resolves micro-op register args by
/// declared name. This is the backward-validation the brief requires: the framework is now
/// register-file-AGNOSTIC, not secretly 6502-shaped. The retired Reg enum (A/X/Y/S) could never
/// have NAMED BC or HL — these specs prove the data-driven path keys on the declared Registers
/// table, not a fixed enum.
///
/// Deliberately a GENERATOR fixture, NOT a shipped CPU and NOT the Z80: the smallest spec whose
/// register names are non-6502, exercising 16-bit registers for storage + transfer + introspection
/// (the generic surface M3.1a owns). It avoids every out-of-scope op — no 16-bit Increment/SetNZ
/// math (M3.4), no flags, no prefix/decode (M3.1b).</summary>
public class SyntheticRegisterSetTests
{
    private const string TinyTestCpuSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("tinytest")]
        public static class TinyTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("BC", 16),
                new("HL", 16),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions =
            [
                // 16-bit storage + transfer + introspection — the generic surface this plan owns.
                // (NO 16-bit Increment/SetNZ — that math is M3.4, out of scope.)
                Insn(0x01, "LDBC", AddrMode.Immediate, [Load("BC")]),
                Insn(0x60, "MOV",  AddrMode.Implied,   [Transfer("HL", "BC")]),
                Insn(0xEA, "NOP",  AddrMode.Implied,   []),
            ];
        }

        public sealed partial class TinyTestCpu
        {
            private readonly IAddressSpace _bus;
            public TinyTestCpu(IAddressSpace bus) => _bus = bus;
            public void Reset() { }
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void Synthetic_spec_with_BC_HL_generates_a_compiling_class()
    {
        var result = GeneratorTestHost.Run(TinyTestCpuSpec);

        Assert.Empty(result.AllErrors);
        // 16-bit fields by declared width — Ground truth C (the emitter types by Bits).
        Assert.Contains("public ushort BC;", result.GeneratedText);
        Assert.Contains("public ushort HL;", result.GeneratedText);
    }

    [Fact]
    public void GetRegister_and_SetRegister_round_trip_BC()
    {
        var result = GeneratorTestHost.Run(TinyTestCpuSpec);

        Assert.Empty(result.AllErrors);
        // Introspection is emitted BY DECLARED NAME — the generated switch arms name "BC"/"HL",
        // 16-bit (ushort) cast on set. The 6502 never had these names; the data-driven path does.
        Assert.Contains("\"BC\" => BC,", result.GeneratedText);
        Assert.Contains("\"HL\" => HL,", result.GeneratedText);
        Assert.Contains("case \"BC\": BC = unchecked((ushort)value); break;", result.GeneratedText);
        Assert.Contains("private static readonly string[] s_registerNames = [\"BC\", \"HL\", \"PC\"];",
            result.GeneratedText);
    }

    [Fact]
    public void Transfer_HL_to_BC_emits_a_field_copy_with_no_AXY_assumption()
    {
        var result = GeneratorTestHost.Run(TinyTestCpuSpec);

        Assert.Empty(result.AllErrors);
        // MOV (0x60) body: Transfer("HL","BC") -> "BC = HL;" — a plain ushort-to-ushort copy of
        // arbitrary declared names. The retired RegIndex map (A=0/X=1/Y=2/S=3) would have thrown
        // on BC/HL; the name-resolving path writes the names straight through.
        Assert.Contains("BC = HL;", result.GeneratedText);
    }

    [Fact]
    public void Register_arg_naming_an_undeclared_register_reports_CPUGEN008()
    {
        // Author Load("IX") — IX is NOT in the synthetic Registers table. CPUGEN008 (the primary
        // register-name gate) fires on an arbitrary NON-6502 name; Reg.IX could never even have
        // been written under the old enum, so this proves the check is purely data-driven.
        string source = GeneratorTestHost.ReplaceSection(
            TinyTestCpuSpec,
            """Insn(0x01, "LDBC", AddrMode.Immediate, [Load("BC")]),""",
            """Insn(0x01, "LDIX", AddrMode.Immediate, [Load("IX")]),""");

        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN008" &&
            d.GetMessage().Contains("IX"));
    }
}
