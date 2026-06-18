using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.Klaus;

/// <summary>
/// Task 7 headline proof: the Klaus 6502 functional test run to its success trap ($3469) through
/// the Tier-1 <see cref="JittedCpu"/>, driven by <see cref="JittedCpu.Run"/> (the JIT's value is
/// Run, not the per-Step interpreter loop). This exercises the full block machinery — discovery,
/// compile, PC-keyed cache, fastmem split, budget exit, block-entry interrupt check, and dirty-page
/// invalidation — under the heaviest available load (Klaus self-modifies RAM and is ADC/SBC-heavy,
/// so it stresses both Task 5 invalidation and the interpreter-fallback path).
///
/// The cycle count is reported and asserted to equal the interpreter anchor <c>96,241,367</c>
/// EXACTLY: the JIT charges the same cycle templates (+ page-cross +1) for emitted instructions,
/// and every ADC/SBC/BRK/RTI fallback runs an interpreter Step that charges the interpreter's
/// exact cycles. A divergence here would be a JIT cycle-accounting bug, not an accepted tolerance.
/// </summary>
public class KlausJitFunctionalTests(ITestOutputHelper output)
{
    private const ushort SuccessTrap = 0x3469;
    private const ushort StartAddress = 0x0400;
    private const long CycleBudget = 500_000_000;       // a passing run needs ~96M cycles
    private const long InterpreterAnchorCycles = 96_241_367; // PR #8/#10/#11 actual

    // Bulk slice for the JIT's fast block-cached run; the budget-1 tail (below) covers the final
    // approach so the JMP-self success trap is detected EXACTLY (a large slice would let the trap
    // spin the remaining budget, overshooting). The trap is reached only at the very end, so the
    // entire bulk runs at slice granularity and only the last TailWindow cycles run at budget-1.
    private const long BulkSlice = 2_000_000;
    private const long TailWindow = 250_000; // budget-1 walk distance to the trap (a few seconds)

    [KlausJitFact]
    public void Functional_test_runs_to_the_success_trap_under_the_JIT()
    {
        byte[] image = File.ReadAllBytes(KlausVectors.TryGetBinaryPath()!);
        Assert.Equal(0x10000, image.Length);

        // The checkpoint is derived from the PINNED interpreter anchor (no redundant ~96M-cycle interp re-run —
        // lever 5). InterpreterAnchorCycles is the committed oracle; the interpreter Klaus PIN (KlausFunctionalTests)
        // still re-verifies it every run, so this constant cannot silently drift.
        long checkpoint = InterpreterAnchorCycles - TailWindow;

        // ── The JIT run (ONCE): chaining ON (the default — confirm the test does NOT disable it).
        // Large block-cached slices to the checkpoint, then budget-1 to the trap. M2-ii's chaining +
        // emitted decimal ADC/SBC make this a large multiple faster than the M2-i 40.9-min fallback +
        // dispatcher-round-trip run; the wall-clock is REPORTED (not asserted — machine-dependent and
        // a flaky pin). The cycle anchor (96,241,367) IS asserted EXACTLY: chaining changes control
        // transfer, not cycle charging, and the emitted decimal arms charge the same cycles as binary.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (inner, jit) = NewKlausJit(image);
        Assert.False(new JitOptions().DisableChaining, "the Klaus JIT pin must run with chaining ON");
        while (inner.CycleCount < checkpoint)
        {
            long budget = Math.Min(BulkSlice, checkpoint - inner.CycleCount);
            jit.Run(ref budget); // exits at a block boundary at/just past the slice edge (no trap yet)
        }
        while (inner.CycleCount < CycleBudget)
        {
            ushort before = inner.PC;
            long budget = 1; // one instruction — exact trap detection (the interpreter twin's idiom)
            jit.Run(ref budget);
            if (inner.PC == before) // trap idiom: jmp * parks PC
            {
                if (inner.PC == SuccessTrap)
                {
                    sw.Stop();
                    output.WriteLine($"JIT success trap reached after {inner.CycleCount} cycles " +
                                     $"in {sw.Elapsed.TotalSeconds:F1}s wall-clock (chaining ON; " +
                                     $"M2-i was 40.9 min)");
                    Assert.Equal(InterpreterAnchorCycles, inner.CycleCount);
                    return;
                }
                Assert.Fail(TrapReport(inner, image, "jit"));
            }
        }
        Assert.Fail($"JIT budget exhausted without parking — PC=0x{inner.PC:X4} " +
                    $"after {inner.CycleCount} cycles");
    }

    /// <summary>A bounded-slice Klaus run with <see cref="JitOptions.DisableChaining"/> reaches the
    /// SAME cycle count as the chaining-on run at the same checkpoint — proving chaining is
    /// transparent to cycle accounting on the heaviest real workload (it changes control transfer,
    /// not the cycle charge). A bounded checkpoint (not the full 96M run) keeps this cheaper than the
    /// headline pin while still exercising millions of chained/unchained cycles.</summary>
    [KlausFact]
    public void Chaining_on_and_off_reach_the_same_Klaus_cycle_count_at_a_checkpoint()
    {
        byte[] image = File.ReadAllBytes(KlausVectors.TryGetBinaryPath()!);
        const long Checkpoint = 20_000_000;   // ~20M cycles into the run — well past warmup

        long RunToCheckpoint(JitOptions opts)
        {
            var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
            space.MapMemory(0x0000, (byte[])image.Clone(), writable: true);
            var inner = new Mos6502Cpu(space) { PC = StartAddress, S = 0xFD, P = 0x34 };
            var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, options: opts);
            while (inner.CycleCount < Checkpoint)
            {
                long budget = Math.Min(BulkSlice, Checkpoint - inner.CycleCount);
                jit.Run(ref budget);
            }
            return inner.CycleCount;
        }

        long on  = RunToCheckpoint(new JitOptions());                            // chaining ON
        long off = RunToCheckpoint(new JitOptions { DisableChaining = true });   // chaining OFF
        output.WriteLine($"chaining on={on} off={off} cycles at the checkpoint");
        Assert.Equal(off, on);   // identical cycle accounting regardless of chaining
    }

    private static (Mos6502Cpu Inner, JittedCpu<Mos6502Cpu> Jit) NewKlausJit(byte[] image)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, image, writable: true); // the test self-modifies RAM
        var inner = new Mos6502Cpu(space) { PC = StartAddress, S = 0xFD, P = 0x34 };
        return (inner, new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space));
    }

    private static string TrapReport(Mos6502Cpu cpu, byte[] image, string phase)
    {
        ushort pc = cpu.PC;
        byte At(int a) => image[a & 0xFFFF];
        string disassembly = Mos6502Cpu.Disassemble(At(pc), At(pc + 1), At(pc + 2));
        return $"JIT trapped ({phase}) at 0x{pc:X4} ({disassembly}) after {cpu.CycleCount} cycles — " +
               $"A=0x{cpu.A:X2} X=0x{cpu.X:X2} Y=0x{cpu.Y:X2} S=0x{cpu.S:X2} P=0x{cpu.P:X2}";
    }
}
