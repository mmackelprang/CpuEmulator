using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M6 PR-S — the SMC/recompile-cost lever. The lever is a PERFORMANCE policy: a block PC that
/// recompiles past the cap (the self-modifying-code thrash signature) runs via the interpreter oracle
/// for a cooldown window instead of recompiling every dispatch. These pins prove (1) the lever is
/// parity-transparent (the interpreter is the oracle, so the result is byte-identical with the lever ON
/// or OFF), (2) the lever sharply DROPS the recompile count on a thrashing program (the W1 fix's
/// mechanism, asserted as a committed artifact — ADR 0011 §3.4), and (3) it actually trips (marks a PC
/// SMC-hot) on a thrasher.</summary>
public class SmcRecompileLeverTests
{
    private static AddressSpace NewRamSpace()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    private static void Poke(AddressSpace space, ushort at, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            space.Write8((uint)(at + i), bytes[i]);
    }

    /// <summary>A tight self-modifying loop at $0200: it stores a (harmless, self-consistent) opcode
    /// byte INTO its own code page every iteration, then loops — the per-dispatch thrash signature.
    /// LDX #count seeds the loop trip; each pass: STA $0203 (patch a NOP-class byte onto this very
    /// page → dirty-mark → SMC guard → evict+recompile), DEX, BNE back, then JMP-self to park.</summary>
    private static (Mos6502Cpu Cpu, JittedCpu<Mos6502Cpu> Jit) NewThrasher(JitOptions opts, byte trips)
    {
        var space = NewRamSpace();
        // $0200: LDX #trips
        // $0202: LDA #$EA           (NOP opcode value — patching it in is self-consistent)
        // $0204: STA $0207          (store onto THIS page $02 — the SMC thrash; $0207 is the NOP slot)
        // $0207: EA (NOP)           (the patched-every-pass byte)
        // $0208: DEX
        // $0209: BNE $0202          (loop — recompiles the block each pass)
        // $020B: JMP $020B          (park)
        Poke(space, 0x0200, 0xA2, trips, 0xA9, 0xEA, 0x8D, 0x07, 0x02, 0xEA,
                            0xCA, 0xD0, 0xF7, 0x4C, 0x0B, 0x02);
        var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = 0x0200, S = 0xFD, P = 0x34 };
        return (cpu, new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space, options: opts));
    }

    private static void DriveToPark(Mos6502Cpu cpu, JittedCpu<Mos6502Cpu> jit)
    {
        // Drive the JIT in small slices to the program's JMP-self park. A slice can END mid-loop with
        // PC == the slice-start PC purely because the cycle budget ran out at that boundary (e.g. on the
        // BNE), which is NOT a park — so "PC unchanged across a slice" alone gives a FALSE park (a
        // truncated run that never reaches the recompile cap). Confirm a real park with a one-cycle
        // probe: at the JMP-self the PC re-lands on itself (still unchanged); at a budget boundary the
        // next instruction advances PC. Only a PC that is unchanged across BOTH the slice and the probe
        // is the JMP-self park.
        while (cpu.CycleCount < 5_000_000)
        {
            ushort before = cpu.PC;
            long budget = 64;
            jit.Run(ref budget);
            if (cpu.PC != before) continue;          // made progress — keep driving
            ushort atRest = cpu.PC;
            long probe = 1;
            jit.Run(ref probe);                      // one more cycle of work
            if (cpu.PC == atRest) return;            // still on the same PC -> JMP-self park
        }
    }

    [Fact]
    public void Lever_on_and_off_reach_the_same_state_and_cycles()
    {
        // Parity: the lever changes the tier a hot PC runs on, NOT the result. On the SAME thrasher,
        // lever-on and lever-off must agree on final A/X/P/PC + cycles (the interpreter is the oracle).
        var (onCpu, onJit) = NewThrasher(new JitOptions(), trips: 200);
        var (offCpu, offJit) = NewThrasher(new JitOptions { DisableSmcLever = true }, trips: 200);
        DriveToPark(onCpu, onJit);
        DriveToPark(offCpu, offJit);
        Assert.Equal(offCpu.A, onCpu.A);
        Assert.Equal(offCpu.X, onCpu.X);
        Assert.Equal(offCpu.P, onCpu.P);
        Assert.Equal(offCpu.PC, onCpu.PC);
        Assert.Equal(offCpu.CycleCount, onCpu.CycleCount);
    }

    [Fact]
    public void Lever_drops_the_recompile_count_sharply_on_a_thrasher()
    {
        // THE COMMITTED ARTIFACT (ADR 0011 §3.4): on an SMC-thrash program, the lever-ON recompile
        // count is far below the lever-OFF count — the mechanism behind the W1 fix, asserted.
        var (offCpu, offJit) = NewThrasher(new JitOptions { DisableSmcLever = true }, trips: 200);
        DriveToPark(offCpu, offJit);
        long off = offJit.TotalRecompiles;

        var (onCpu, onJit) = NewThrasher(new JitOptions(), trips: 200);
        DriveToPark(onCpu, onJit);
        long on = onJit.TotalRecompiles;

        // Lever-off recompiles ~once per loop trip (the thrash); lever-on caps recompiles then runs the
        // interpreter, so its recompile count is a small fraction. Assert a sharp drop (>= 4x fewer) and
        // that lever-off actually thrashed (so the comparison is non-vacuous).
        Assert.True(off > 50, $"lever-off should thrash (recompile per trip); got {off}");
        Assert.True(on * 4 < off, $"lever-on recompiles {on} should be <<  lever-off {off} (sharp drop)");
    }

    [Fact]
    public void Lever_marks_a_thrashing_pc_smc_hot()
    {
        // Non-vacuity: the lever actually trips on a thrasher (>= 1 PC marked SMC-hot), so the drop
        // above is the lever firing, not an artifact of a short run.
        var (cpu, jit) = NewThrasher(new JitOptions(), trips: 200);
        DriveToPark(cpu, jit);
        Assert.True(jit.SmcHotPcCount >= 1, "the thrasher should mark at least one PC SMC-hot");
    }

    [Fact]
    public void Non_smc_program_never_trips_the_lever()
    {
        // A non-self-modifying loop (stores to a DATA page, not code) must never trip the lever — its
        // block compiles once and runs; the recompile counter stays 0, the byte-identical-W2/W3 guard.
        var space = NewRamSpace();
        // $0200: LDX #200 / LDA #$01 / STA $4000 (DATA page, no SMC) / DEX / BNE $0202 / JMP-self
        Poke(space, 0x0200, 0xA2, 0xC8, 0xA9, 0x01, 0x8D, 0x00, 0x40, 0xCA, 0xD0, 0xF8, 0x4C, 0x0A, 0x02);
        var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = 0x0200, S = 0xFD, P = 0x34 };
        var jit = new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space);
        DriveToPark(cpu, jit);
        Assert.Equal(0, jit.SmcHotPcCount);
        Assert.Equal(0L, jit.TotalRecompiles);
    }

    [Fact]
    public void W2_W3_shaped_kernels_never_trip_the_lever()
    {
        // W2/W3 are SMC-free (stores hit DATA pages). Run the committed W2 + W3 kernel images under the
        // JIT for a bounded window and assert the lever never trips (SmcHotPcCount == 0) — the
        // byte-identical-to-today guard for the compute workloads the emit PRs lifted.
        foreach (var w in new[] { CpuEmulator.Benchmarks.Workloads.ArithmeticKernel(),
                                  CpuEmulator.Benchmarks.Workloads.SieveKernel() })
        {
            var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
            space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
            var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };
            var jit = new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space);
            long budget = 5_000_000;
            jit.Run(ref budget);
            Assert.Equal(0, jit.SmcHotPcCount);   // SMC-free → the lever never fires
        }
    }
}
