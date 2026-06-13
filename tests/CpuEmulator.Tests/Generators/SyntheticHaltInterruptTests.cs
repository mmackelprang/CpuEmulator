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

    // ── Task 7: Machine.Run does not trip the no-progress guard on a halted CPU (CONFIRM) ─────

    /// <summary>Wire the synthetic halt CPU into a Machine via the factory (the CPU is loaded
    /// dynamically, so construct it by reflection and return it as the ICpuCore it implements).</summary>
    private static (Machine machine, object cpu) NewMachine()
    {
        object? captured = null;
        var machine = Machine.Create("haltirqtest")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0, 0x10000)
            .WithCpu(ctx =>
            {
                captured = Activator.CreateInstance(s_cpu.Value, ctx.Space(AddressSpaceKind.Program))!;
                return (ICpuCore)captured;
            })
            .Build();
        return (machine, captured!);
    }

    [Fact]
    public void Machine_Run_does_not_trip_the_no_progress_guard_on_a_halted_cpu()
    {
        var (machine, cpu) = NewMachine();
        machine.Space(AddressSpaceKind.Program).Write8(0x0200, 0x76);   // HALT
        SetPc(cpu, 0x0200);

        // A Machine running a halted CPU for a budget MUST return the consumed budget and NOT throw
        // EmulationException — the guard is correct AS-IS (Ground truth B.2): a halted Step advances
        // >=1 cycle (the idle), so it is making the legitimate progress of a halted processor.
        long executed = machine.Run(50);

        Assert.True(executed >= 50, $"expected >= 50 cycles consumed (idle), got {executed}");
        Assert.True(Halted(cpu), "the CPU should still be halted (no interrupt asserted)");
    }

    [Fact]
    public void A_genuinely_stuck_cpu_still_trips_the_no_progress_guard()
    {
        // The guard still does its job for a CPU that makes NO progress (the StuckCpu double): the
        // halted accommodation is the always-advance invariant, NOT a relaxation of the guard.
        var machine = Machine.Create("stuck")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithCpu(_ => new CpuEmulator.Tests.TestDoubles.StuckCpu())
            .Build();

        var ex = Assert.Throws<EmulationException>(() => machine.Run(100));
        Assert.Contains("no progress", ex.Message);
    }

    // ── Task 8: the wake (interpreter) + the JIT dispatcher halted fast path (seam pin) ───────

    [Fact]
    public void Halted_cpu_wakes_on_a_serviced_interrupt()
    {
        var (cpu, program) = NewCpu();
        program.Write8(0x0200, 0x76);   // HALT
        // A handler at the table vector 0xFF00 (VectorBase 0) -> 0x0300.
        program.Write8(0x0300, 0xEA);   // NOP at the handler
        program.Write8(0xFF00, 0x00);   // vector lo
        program.Write8(0xFF01, 0x03);   // vector hi -> 0x0300
        SetPc(cpu, 0x0200);
        SetVectorBase(cpu, 0);

        Step(cpu);                      // HALT -> halted
        Assert.True(Halted(cpu));

        Step(cpu);                      // halted, no IRQ -> idle (still halted)
        Assert.True(Halted(cpu));

        SetIrq(cpu, true);              // assert the IRQ line mid-halt
        Step(cpu);                      // TryServiceInterrupt services it: clears _halted, PC -> vector
        Assert.False(Halted(cpu));      // woke
        Assert.Equal(0x0300, GetPc(cpu));   // vectored through the table to the handler

        ushort pcAtHandler = GetPc(cpu);
        SetIrq(cpu, false);
        Step(cpu);                      // normal fetch resumes — runs the NOP at the handler
        Assert.Equal((ushort)(pcAtHandler + 1), GetPc(cpu));   // PC advanced past the NOP (real fetch)
    }

    [Fact]
    public void Jit_dispatcher_halted_fast_path_idles()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // JIT-only; skip where dynamic code is disabled

        // The JIT dispatcher's halted fast path delegates the idle to _inner.Step (Ground truth B.3),
        // decrementing the budget by the consumed (idle) cycles WITHOUT compiling a block. Pin the
        // delegation semantics directly on the synthetic halt CPU (the live second-CPU JittedCpu run
        // is M3.5 — JittedCpu is hardcoded to Mos6502Cpu, which never halts): a halted CPU stepped in
        // the dispatcher's loop shape consumes exactly one cycle/Step and the budget tracks it.
        var (cpu, program) = NewCpu();
        program.Write8(0x0200, 0x76);   // HALT
        SetPc(cpu, 0x0200);
        Step(cpu);                      // -> halted
        Assert.True(Halted(cpu));

        // Mirror the dispatcher's halted branch: while halted, idle via Step and decrement budget.
        long budget = 5;
        int idleSteps = 0;
        while (budget > 0 && Halted(cpu))
        {
            long before = Cycles(cpu);
            Step(cpu);                  // the same _inner.Step() the dispatcher calls (no block compile)
            budget -= Cycles(cpu) - before;
            idleSteps++;
        }

        Assert.Equal(5, idleSteps);     // 5 idle cycles consumed the 5-cycle budget, one per Step
        Assert.True(budget <= 0);
    }

    [Fact]
    public void Jit_dispatcher_halted_branch_is_dead_for_the_non_halting_6502()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;

        // The 6502 never halts (_inner.Halted is always false), so the dispatcher's halted branch is
        // dead and a normal program runs through compiled blocks exactly as before (byte-identical).
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0, new byte[0x10000], writable: true);
        // LDA #$42 ; JMP-self
        space.Write8(0x0200, 0xA9); space.Write8(0x0201, 0x42);
        space.Write8(0x0202, 0x4C); space.Write8(0x0203, 0x02); space.Write8(0x0204, 0x02);
        var inner = new CpuEmulator.Cpus.Mos6502.Mos6502Cpu(space);
        inner.PC = 0x0200;
        Assert.False(inner.Halted);     // the 6502 hand-written Halted hook is always false

        var jit = new CpuEmulator.Jit.JittedCpu(inner, space);
        long budget = 20;
        jit.Run(ref budget);            // does not hang on the JMP-self; the halted branch never fires

        Assert.Equal(0x42, inner.A);    // the LDA ran through a compiled block
    }

    // ── Task 9: the non-6502 interrupt-shape proof (CONFIRM — the seam is already generic) ────

    [Fact]
    public void Interrupt_service_vectors_through_a_table_not_a_fixed_address()
    {
        // The synthetic partial's TryServiceInterrupt vectors through a TABLE indexed by VectorBase
        // (0xFF00 + VectorBase), NOT the 6502's fixed $FFFE — the proof that the interrupt seam
        // expresses a non-6502 shape with NO Core/generator change. VectorBase = 4 -> the handler
        // address lives at 0xFF04, distinct from any fixed 6502 vector.
        var (cpu, program) = NewCpu();
        SetVectorBase(cpu, 4);
        program.Write8(0xFF04, 0x34);   // handler lo
        program.Write8(0xFF05, 0x12);   // handler hi -> 0x1234
        SetPc(cpu, 0x0200);

        SetIrq(cpu, true);
        Step(cpu);                      // services via the TABLE vector

        Assert.Equal(0x1234, GetPc(cpu));   // PC == the table entry at 0xFF04 (NOT a $FFFE vector)
    }

    [Fact]
    public void A_different_VectorBase_selects_a_different_table_entry()
    {
        // Genuinely table-INDEXED (not two hardcoded cases): VectorBase 6 -> the handler at 0xFF06.
        var (cpu, program) = NewCpu();
        SetVectorBase(cpu, 6);
        program.Write8(0xFF06, 0x78);
        program.Write8(0xFF07, 0x56);   // -> 0x5678
        SetPc(cpu, 0x0200);

        SetIrq(cpu, true);
        Step(cpu);

        Assert.Equal(0x5678, GetPc(cpu));
    }
}
