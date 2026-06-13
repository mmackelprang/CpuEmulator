using CpuEmulator.Core.Jit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M3.2 Tasks 1-3 — the additive port/halt VOCABULARY (modes, micro-ops, the Port class)
/// proven at the generator + JIT-data-layer level, in isolation from the full synthetic CPU. These
/// are the smallest red→green units: the new AddrMode/JitMode members round-trip; a row using
/// PortIn("A")/Halt() is recognized; a Port-class row classifies + is mode-gated. None of this
/// touches a 6502 path (the 6502 declares no port op, no halt) — the byte-identical-6502 invariant
/// (Ground truth E) is pinned by the unchanged generator snapshot + the .g.cs hash at Task 10.</summary>
public class PortHaltVocabularyTests
{
    // ── Task 1: the IoPort* modes exist in the JIT data-layer mirror (JitMode) ────────────────

    [Fact]
    public void JitMode_admits_the_two_IoPort_modes()
    {
        // The JIT data-layer copy of AddrMode (JitMode) gains IoPortImmediate/IoPortIndirect —
        // additive enum members the 6502 never names (Ground truth A.2 mirror-table tax).
        Assert.True(System.Enum.IsDefined(typeof(JitMode), JitMode.IoPortImmediate));
        Assert.True(System.Enum.IsDefined(typeof(JitMode), JitMode.IoPortIndirect));
    }

    [Fact]
    public void OpcodeDescriptor_round_trips_an_IoPort_mode()
    {
        // A descriptor constructed with the new mode round-trips it — the record shape is UNCHANGED
        // (the port op rides JitOp.Kind, no new field; Ground truth A.3), so this is a pure
        // additive-enum-value proof. (The Port CLASS is Task 3 — proven there.)
        var d = new OpcodeDescriptor(
            0xDB, "IN", JitMode.IoPortImmediate, JitOpClass.Load,
            LengthRule.Fixed, FixedLength: 2, BaseCycles: 3, PageCrossPenalty: false,
            NeedsFallback: false, EndsBlock: false,
            Ops: [new JitOp("PortIn", "A", "", 0, false)]);

        Assert.Equal(JitMode.IoPortImmediate, d.Mode);
        Assert.Equal("PortIn", d.Ops[0].Kind);
        Assert.Equal("A", d.Ops[0].RegA);
    }

    // ── Task 2: the PortIn/PortOut/Halt micro-op factories are recognized by the generator ────

    private const string PortHaltSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("vocabtest")]
        public static class VocabTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDB, "IN",   AddrMode.IoPortImmediate, [PortIn("A")]),
                Insn(0xD3, "OUT",  AddrMode.IoPortImmediate, [PortOut("A")]),
                Insn(0x76, "HALT", AddrMode.Implied,         [Halt()]),
                Insn(0xEA, "NOP",  AddrMode.Implied,         []),
            ];
        }

        public sealed partial class VocabTestCpu
        {
            private readonly IAddressSpace _bus;
            private readonly IAddressSpace _ioBus;
            private bool _halted;
            public VocabTestCpu(IAddressSpace bus, IAddressSpace ioBus) { _bus = bus; _ioBus = ioBus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint a) { _cycles++; return _bus.Read8(a); }
            private void WriteBus(uint a, byte v) { _cycles++; _bus.Write8(a, v); }
            private byte ReadIo(uint p) { _cycles++; return _ioBus.Read8(p); }
            private void WriteIo(uint p, byte v) { _cycles++; _ioBus.Write8(p, v); }
            private void IdleCycle() { _cycles++; }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            public partial bool Halted => _halted;
            private void DoHalt() { _halted = true; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void PortIn_PortOut_Halt_are_recognized_micro_ops()
    {
        // Task 2: the three factories exist (the spec source compiles the Spec.PortIn/PortOut/Halt
        // calls) and the generator recognizes their names — no CPUGEN "unknown micro-op" diagnostic.
        // (Full generate-clean — the Port class + the interpreter/Halt bodies — lands at Tasks 3/4/6;
        // this unit isolates the vocabulary recognition only.)
        var result = GeneratorTestHost.Run(PortHaltSpec);

        // The names are KNOWN micro-ops — no CPUGEN006 "unknown micro-op". (Classification into the
        // Port class is Task 3; the CPUGEN010 mode/op-combination diagnostic until then is expected.)
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "CPUGEN006");
        // No C# compile error from a MISSING Spec.PortIn/PortOut/Halt factory (CS0103) — the
        // factories exist and resolve. (Downstream CS errors from the not-yet-emitted Port/Halt
        // bodies are Tasks 4/6; the factory-resolution gate is what Task 2 proves.)
        Assert.DoesNotContain(result.AllErrors,
            d => d.Id == "CS0103" &&
                 (d.GetMessage().Contains("PortIn") || d.GetMessage().Contains("PortOut")
               || d.GetMessage().Contains("Halt")));
    }

    // ── Task 3: the Port class — mode-legality gate (CPUGEN010) ───────────────────────────────

    /// <summary>A spec whose ops + modes are EDITABLE per-test, so the Port-class mode-legality
    /// gate can be exercised positive (IoPort* accepted) and negative (a port op in Absolute
    /// rejected). The partial supplies the port-bus + halt hooks so the generated class compiles
    /// once the bodies land (Tasks 4/6); for the mode-gate tests only the GENERATOR diagnostics
    /// are read, so a downstream CS error before Task 4/6 does not affect the CPUGEN010 assertions.</summary>
    private static string PortModeSpec(string row) => $$"""
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("portmodetest")]
        public static class PortModeTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions =
            [
                {{row}}
                Insn(0xEA, "NOP", AddrMode.Implied, []),
            ];
        }

        public sealed partial class PortModeTestCpu
        {
            private readonly IAddressSpace _bus;
            private readonly IAddressSpace _ioBus;
            public PortModeTestCpu(IAddressSpace bus, IAddressSpace ioBus) { _bus = bus; _ioBus = ioBus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint a) { _cycles++; return _bus.Read8(a); }
            private void WriteBus(uint a, byte v) { _cycles++; _bus.Write8(a, v); }
            private byte ReadIo(uint p) { _cycles++; return _ioBus.Read8(p); }
            private void WriteIo(uint p, byte v) { _cycles++; _ioBus.Write8(p, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void PortIn_in_IoPortImmediate_mode_is_accepted()
    {
        var result = GeneratorTestHost.Run(
            PortModeSpec("""Insn(0xDB, "IN", AddrMode.IoPortImmediate, [PortIn("A")]),"""));

        // The Port class accepts the IoPort* modes — no CPUGEN010 mode/op-combination diagnostic.
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void PortOut_in_IoPortIndirect_mode_is_accepted()
    {
        var result = GeneratorTestHost.Run(
            PortModeSpec("""Insn(0xED, "OUT", AddrMode.IoPortIndirect, [PortOut("A")]),"""));

        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Port_op_in_a_non_port_mode_is_a_CPUGEN010_diagnostic()
    {
        // A port op in Absolute mode is rejected — the per-class mode-legality gate every class has.
        var result = GeneratorTestHost.Run(
            PortModeSpec("""Insn(0xDB, "IN", AddrMode.Absolute, [PortIn("A")]),"""));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void A_non_port_op_in_an_IoPort_mode_is_a_CPUGEN010_diagnostic()
    {
        // The gate is symmetric: a Load in an IoPort* mode is rejected (IoPort* is Port-class only).
        var result = GeneratorTestHost.Run(
            PortModeSpec("""Insn(0xA9, "LDA", AddrMode.IoPortImmediate, [Load("A")]),"""));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }
}
