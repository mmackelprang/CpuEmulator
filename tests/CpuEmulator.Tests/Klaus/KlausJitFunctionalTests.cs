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

    [KlausFact]
    public void Functional_test_runs_to_the_success_trap_under_the_JIT()
    {
        byte[] image = File.ReadAllBytes(KlausVectors.TryGetBinaryPath()!);
        Assert.Equal(0x10000, image.Length);

        // ── Reference: the interpreter run to the trap (fast — the interpreter is the oracle) ──────
        // Establishes the live trap-entry cycle count, and the checkpoint just before it from which
        // the JIT switches to budget-1 for an exact trap detection.
        long anchorCycles = RunInterpreterToTrap(image);
        Assert.Equal(InterpreterAnchorCycles, anchorCycles); // the interpreter still hits its anchor
        long checkpoint = anchorCycles - TailWindow;

        // ── The JIT run (ONCE): large block-cached slices to the checkpoint, then budget-1 to trap ──
        var (inner, jit) = NewKlausJit(image);
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
                    output.WriteLine($"JIT success trap reached after {inner.CycleCount} cycles");
                    Assert.Equal(InterpreterAnchorCycles, inner.CycleCount);
                    return;
                }
                Assert.Fail(TrapReport(inner, image, "jit"));
            }
        }
        Assert.Fail($"JIT budget exhausted without parking — PC=0x{inner.PC:X4} " +
                    $"after {inner.CycleCount} cycles");
    }

    private static long RunInterpreterToTrap(byte[] image)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, image, writable: true);
        var cpu = new Mos6502Cpu(space) { PC = StartAddress, S = 0xFD, P = 0x34 };
        while (cpu.CycleCount < CycleBudget)
        {
            ushort before = cpu.PC;
            cpu.Step();
            if (cpu.PC == before)
            {
                Assert.Equal(SuccessTrap, cpu.PC); // a non-success trap is a real failure
                return cpu.CycleCount;
            }
        }
        Assert.Fail($"interpreter reference exhausted budget at PC=0x{cpu.PC:X4}");
        return -1; // unreachable
    }

    private static (Mos6502Cpu Inner, JittedCpu Jit) NewKlausJit(byte[] image)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, image, writable: true); // the test self-modifies RAM
        var inner = new Mos6502Cpu(space) { PC = StartAddress, S = 0xFD, P = 0x34 };
        return (inner, new JittedCpu(inner, space));
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
