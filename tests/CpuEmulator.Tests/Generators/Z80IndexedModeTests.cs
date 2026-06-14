using CpuEmulator.Core.Jit;
using CpuEmulator.Core.Specification;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M3.4e-1a — the <c>Indexed</c> AddrMode is a first-class declarable mode (the four mirror tables
/// move together: AddrMode + JitMode + s_addrModes + SupportedModes, plus the ModeLength("Indexed")=3
/// arm). Proven the way M3.4b proved the <c>Bit</c> mode (Z80CbModeTests): enum-membership for both
/// the spec-authoring AddrMode and the JIT-data JitMode, PLUS the parser-level membership gate —
/// an <c>Indexed</c>-mode row PARSES past the s_addrModes.Contains check (SpecParser.cs:640) and is
/// then (correctly) rejected only at the class/mode legality check, because NO op-class accepts
/// <c>Indexed</c> yet (RECON-FINDING A4: the class-widening is M3.4e-2, when the real (IX+d) ops land).
/// e-1a makes <c>Indexed</c> declarable; it makes no DD/FD opcode live.
/// </summary>
public class Z80IndexedModeTests
{
    [Fact]
    public void AddrMode_has_Indexed_member()
    {
        Assert.True(System.Enum.IsDefined(typeof(AddrMode), "Indexed"));
    }

    [Fact]
    public void JitMode_has_Indexed_member()
    {
        Assert.True(System.Enum.IsDefined(typeof(JitMode), "Indexed"));
    }

    // A minimal Z80-shaped spec scaffold (mirrors Z80AddrModeParserTests). The {INSN} placeholder is
    // replaced per-test. Carries an IX register so an Indexed-mode row's index-register requirement
    // (if any) is satisfiable and the rejection we observe is the class/mode one, not a missing-register.
    private const string Scaffold = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticZ80Ixm;

        [CpuSpecification("z80ixm")]
        public static class Z80IxmSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("B", 8), new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16), new("IX", 16),
                new("SP", 16, RegisterRole.StackPointer),
                new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"),
            ];

            public static readonly FlagLayout Flags = new(
                [ new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                  new("X", 3), new("P", 2), new("N", 1), new("C", 0) ]);

            public static readonly InstructionDef[] Instructions =
            [
                {INSN}
            ];
        }

        public sealed partial class Z80IxmCpu
        {
            private readonly IAddressSpace _bus;
            private bool _iff1, _iff2;
            public byte Q;
            public bool Iff1 { get => _iff1; set => _iff1 = value; }
            public bool Iff2 { get => _iff2; set => _iff2 = value; }
            public Z80IxmCpu(IAddressSpace bus) => _bus = bus;
            public void Reset() { }
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
            private byte ReadIo(uint port) { _cycles++; return _bus.Read8(port); }
            private void WriteIo(uint port, byte value) { _cycles++; _bus.Write8(port, value); }
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    private static GeneratorRunResult RunWith(string insns) =>
        GeneratorTestHost.Run(Scaffold.Replace("{INSN}", insns));

    [Fact]
    public void Indexed_parses_as_a_known_mode_but_no_class_accepts_it_yet()
    {
        // An Indexed-mode row PARSES past the s_addrModes.Contains gate (SpecParser.cs:640) — proving
        // "Indexed" is a recognised AddrMode member. It is then rejected ONLY at the class/mode legality
        // check (CPUGEN010), because no op-class accepts Indexed yet (A4: that widening is M3.4e-2). The
        // absence of any "unknown AddrMode member" diagnostic is the parser-membership proof; the
        // presence of the class/mode CPUGEN010 is the honest "no opcode is live" boundary.
        var result = RunWith("""Insn(0xDD, 0x7E, "LD", AddrMode.Indexed, [Transfer("A","A")]),""");

        // The mode is KNOWN — no "unknown AddrMode" diagnostic (that gate is what e-1a opens).
        Assert.DoesNotContain(result.GeneratorDiagnostics,
            d => d.GetMessage().Contains("known AddrMode"));
        // But no class accepts Indexed yet, so the class/mode check rejects it (CPUGEN010).
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }
}
