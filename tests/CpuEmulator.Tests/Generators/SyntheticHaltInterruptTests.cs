using System.Reflection;
using CpuEmulator.Core;

namespace CpuEmulator.Tests.Generators;

/// <summary>M3.2 Ground truth F.2 (Tasks 6-9) — the HALT + NON-6502 INTERRUPT proof. A GENERATOR
/// fixture declaring a Halt() op and a hand-written partial whose TryServiceInterrupt vectors
/// through a TABLE (NOT the 6502 fixed vector), proving: a halted CPU idles one cycle/Step (does
/// not fetch), Machine.Run does not trip the no-progress guard on a halted CPU, the CPU wakes on a
/// serviced interrupt, and the interrupt seam expresses a non-6502 (table-vectored) shape. The
/// 6502 never halts and never table-vectors, so none of this perturbs it (byte-identical .g.cs).</summary>
public class SyntheticHaltInterruptTests
{
    private const string HaltIrqTestCpuSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("haltirqtest")]
        public static class HaltIrqTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x76, "HALT", AddrMode.Implied, [Halt()]),   // the generic halted state
                Insn(0xEA, "NOP", AddrMode.Implied, []),
            ];
        }

        public sealed partial class HaltIrqTestCpu
        {
            private readonly IAddressSpace _bus;
            private bool _halted;
            private bool _intLine;
            public byte VectorBase;                              // the table base — a NON-6502 vectoring input
            public HaltIrqTestCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { _halted = false; }
            public void SetIrqLine(bool a) => _intLine = a;
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint addr) { _cycles++; return _bus.Read8(addr); }
            private void WriteBus(uint addr, byte v) { _cycles++; _bus.Write8(addr, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            private void IdleCycle() { _cycles++; }              // the "NOP while halted" (Ground truth B.1)
            public partial bool Halted => _halted;               // the Step halted hook
            // The Halt() micro-op body sets the latch — the generated Register-arm Halt case calls this:
            private void DoHalt() { _halted = true; }

            public partial bool InterruptPending => _intLine;    // partial-private predicate (non-6502 shape)
            // A NON-6502 interrupt service: vector through a TABLE the partial reads from the bus, indexed
            // by VectorBase — proving the seam is not fixed-vector-shaped. Clears _halted (the wake).
            private partial bool TryServiceInterrupt()
            {
                if (!_intLine) return false;
                _halted = false;                                 // the wake (Z80 HALT clears on INT)
                uint slot = 0xFF00u + VectorBase;                // table-indexed vectoring (NOT $FFFE)
                uint lo = ReadBus(slot), hi = ReadBus(slot + 1);
                PC = (ushort)(lo | (hi << 8));
                return true;
            }
        }
        """;

    private static readonly Lazy<Type> s_cpu =
        new(() => GeneratorTestHost.CompileAndLoadType(HaltIrqTestCpuSpec, "SyntheticCpu.HaltIrqTestCpu"));

    private static (object cpu, AddressSpace program) NewCpu()
    {
        var program = new AddressSpace(AddressSpaceKind.Program, 16);
        program.MapMemory(0, new byte[0x10000], writable: true);
        object cpu = Activator.CreateInstance(s_cpu.Value, program)!;
        return (cpu, program);
    }

    private static void SetPc(object cpu, ushort pc) => s_cpu.Value.GetField("PC")!.SetValue(cpu, pc);
    private static ushort GetPc(object cpu) => (ushort)s_cpu.Value.GetField("PC")!.GetValue(cpu)!;
    private static long Cycles(object cpu) => (long)s_cpu.Value.GetProperty("CycleCount")!.GetValue(cpu)!;
    private static void Step(object cpu) => s_cpu.Value.GetMethod("Step")!.Invoke(cpu, null);
    private static void SetIrq(object cpu, bool a) =>
        s_cpu.Value.GetMethod("SetIrqLine")!.Invoke(cpu, [a]);
    private static void SetVectorBase(object cpu, byte v) =>
        s_cpu.Value.GetField("VectorBase")!.SetValue(cpu, v);
    private static bool Halted(object cpu) => (bool)s_cpu.Value.GetProperty("Halted")!.GetValue(cpu)!;

    // ── Task 6: the Halt() op + the Step halted guard ─────────────────────────────────────────

    [Fact]
    public void Halt_spec_generates_a_compiling_class()
    {
        var result = GeneratorTestHost.Run(HaltIrqTestCpuSpec);

        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n",
                result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
        Assert.Contains("partial class HaltIrqTestCpu", result.GeneratedText);
        // The Step halted guard + the Halted/IdleCycle requirement are emitted (Ground truth B.1).
        Assert.Contains("if (Halted)", result.GeneratedText);
        Assert.Contains("IdleCycle()", result.GeneratedText);
    }

    [Fact]
    public void Halted_cpu_idles_one_cycle_per_step()
    {
        var (cpu, program) = NewCpu();
        program.Write8(0x0200, 0x76);   // HALT
        program.Write8(0x0201, 0xEA);   // NOP (would run if the CPU were not halted)
        SetPc(cpu, 0x0200);

        Step(cpu);                      // executes HALT -> sets the latch
        Assert.True(Halted(cpu));
        Assert.Equal(0x0201, GetPc(cpu));   // PC advanced past the HALT opcode

        long afterHalt = Cycles(cpu);
        ushort pcAfterHalt = GetPc(cpu);
        Step(cpu);                      // halted: idle one cycle, do NOT fetch/advance PC

        Assert.Equal(1, Cycles(cpu) - afterHalt);   // exactly one idle cycle
        Assert.Equal(pcAfterHalt, GetPc(cpu));       // PC did not advance (no fetch)
        Assert.True(Halted(cpu));                    // still halted
    }
}
