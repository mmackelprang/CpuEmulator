using CpuEmulator.SpecImporter;
using CpuEmulator.Tests.Generators;
using Microsoft.CodeAnalysis;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// M3.3 Rung 3 + Rung 4 — the structural-generation check (the Z80 half of ImporterEndToEndTests).
///
/// Runs the importer engine on the committed Z80 data files, appends a MINIMAL hand-written Z80Cpu
/// partial (bus/IO wiring + the policy hooks, NO real semantics), and pushes the result through the
/// real Roslyn generator. Asserts zero generator diagnostics (CPUGEN) and zero compilation errors —
/// proving the dataset + the M3.1b generic decoder + the M3.1a data-driven register file accommodate
/// the Z80's prefix structure end-to-end (the decode SKELETON compiles).
///
/// What this PROVES: the STRUCTURE (the prefix-keyed decode skeleton compiles; ED B0 keys distinctly
/// from base B0 through the generator; the 22 registers feed the data-driven register file). What it
/// does NOT prove: that any instruction EXECUTES correctly (semantics mostly TODO; the covered ops are
/// NOT flag-correct). Execution correctness is M3.4 + TomHarte (unverified-pending-M3.4).
///
/// Console-isolation collection: shares the in-proc Console-redirect discipline with the 6502 e2e.
/// </summary>
[Collection("ConsoleIsolation")]
public class Z80SkeletonEndToEndTests
{
    private static string Z80DatasetPath   => DataPath.Get("z80-opcodes.json");
    private static string Z80SemanticsPath => DataPath.Get("z80-semantics.json");

    // The minimal Z80Cpu partial (mirrors src/CpuEmulator.Cpus.Z80/Z80Cpu.cs). Appended to the emitted
    // skeleton source WITHOUT a namespace header — the emitted source's file-scoped namespace covers it.
    // Provides every hand-written member the generated half requires for a Halt+Port+prefixed CPU:
    // ctor, Reset, SetIrqLine/SetNmiLine, ReadBus/WriteBus, ReadIo/WriteIo, IdleCycle, DoHalt,
    // HandleUndefinedOpcode, TryServiceInterrupt, InterruptPending, Halted. NO real semantics.
    private const string MinimalPartial = """

        public sealed partial class Z80Cpu
        {
            private readonly IAddressSpace _bus;
            private readonly IAddressSpace _io;
            private bool _halted;
            private bool _iff1;
            private bool _iff2;
            public bool Iff1 { get => _iff1; set => _iff1 = value; }
            public bool Iff2 { get => _iff2; set => _iff2 = value; }
            public int Im;
            public byte Q;
            public Z80Cpu(IAddressSpace bus, IAddressSpace? io = null)
            {
                _bus = bus;
                _io  = io ?? new AddressSpace(AddressSpaceKind.Io, 16);
            }
            public void Reset() => _halted = false;
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            public partial bool InterruptPending => false;
            public partial bool Halted => _halted;
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
            private byte ReadIo(uint port) { _cycles++; return _io.Read8(port); }
            private void WriteIo(uint port, byte value) { _cycles++; _io.Write8(port, value); }
            private void IdleCycle() => _cycles++;
            private void DoHalt() => _halted = true;
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes)
            {
                for (int i = 0; i < keyBytes; i++)
                    R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
            }
        }
        """;

    private static string BuildFullSource()
    {
        var dataset = OpcodeDataset.Load(Z80DatasetPath);
        var map     = SemanticsMap.Load(Z80SemanticsPath);
        var (source, _) = SpecImportEngine.Run(dataset, map, "z80-opcodes.json", "z80-semantics.json");

        // Prepend the CpuEmulator.Core using (for IAddressSpace/AddressSpace/AddressSpaceKind), then
        // append the partial under the existing file-scoped namespace.
        var patched = source.Replace(
            "using CpuEmulator.Core.Specification;",
            "using CpuEmulator.Core;\nusing CpuEmulator.Core.Specification;");
        return patched + MinimalPartial;
    }

    [Fact]
    public void Z80_skeleton_generates_with_zero_diagnostics()
    {
        var result = GeneratorTestHost.Run(BuildFullSource());
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.AllErrors);
    }

    [Fact]
    public void Z80_skeleton_declares_all_registers()
    {
        var result = GeneratorTestHost.Run(BuildFullSource());
        Assert.Empty(result.AllErrors);
        // The 22 declared Z80 registers feed the M3.1a data-driven register file → appear in the
        // generated state struct. Spot the main set, the alternate set, the specials, and the 16-bit.
        var gen = result.GeneratedText;
        foreach (var reg in new[] { "\"A\"", "\"F\"", "\"B\"", "\"C\"", "\"D\"", "\"E\"", "\"H\"", "\"L\"",
                                    "\"A_\"", "\"L_\"", "\"I\"", "\"R\"", "\"IX\"", "\"IY\"", "\"SP\"", "\"PC\"" })
            Assert.Contains(reg, gen);
    }

    [Fact]
    public void Z80_skeleton_has_per_plane_decode_skeleton()
    {
        var result = GeneratorTestHost.Run(BuildFullSource());
        Assert.Empty(result.AllErrors);
        var gen = result.GeneratedText;
        // The DecodeStructure produced a decode walk + a prefix-aware key table. A prefixed row (ED NEG)
        // resolves to the plane-qualified key (0xED << 8) | 0x44 = 0xED44 — DISTINCT from any base key
        // (base keys are <= 0xFF). This is the prefix-key non-collision proven end-to-end through the
        // generator (the headline DatasetDiff fix realized in the decode skeleton).
        Assert.Contains("0xED44", gen);
        // The decode walk consumes the fetch stream (the M3.1b generic decoder over the seven planes).
        Assert.Contains("Decode(", gen);
    }

    [Fact]
    public void Z80_base_plane_is_live_ddfd_core_live_compound_live()
    {
        // M3.4a: the BASE plane is LIVE. M3.4d: the ED block ops (0xA0–0xBB) are LIVE. M3.4e-2: the DD/FD
        // CORE plane (252 + 252 opcodes) is LIVE. M3.4e-3: the DDCB/FDCB COMPOUND bit/rotate/shift forms
        // are now LIVE too (the Insn(0xDD, 0xCB, finalOp, …) overload via Z80DdCbSemantics). Confirm the
        // base-plane OR r + an ED block op (LDIR) + DD-core rows + a DDCB compound row are real rows.
        var source = BuildFullSource();
        Assert.Contains("Insn(0xED, 0xB0, \"LDIR\", AddrMode.Implied, [EdBlock(\"LDIR\")]),", source); // LDIR — LIVE (M3.4d)
        Assert.Contains("Insn(0xDD, 0x09, \"ADD\", AddrMode.Register, [Add16(\"IX\",\"BC\")]),", source); // ADD IX,BC — LIVE (M3.4e-2)
        Assert.Contains("Insn(0xDD, 0x84, \"ADD\", AddrMode.Register, [Add8()]),", source);  // ADD A,IXh — LIVE (M3.4e-2)
        Assert.Contains("Insn(0xB0, \"OR\", AddrMode.Register, [Or8()]),", source);  // base OR B — LIVE
        // M3.4e-3: the DDCB compound bit/rotate/shift forms are LIVE (the compound Insn overload).
        Assert.Contains("Insn(0xDD, 0xCB, 0x06, \"RLC\", AddrMode.Indexed, [DdCb(\"RLC\",0,\"-\")]),", source);
        Assert.DoesNotContain("Insn(0xDDCB", source);                  // the prefix is split (0xDD, 0xCB), not a single literal
    }
}
