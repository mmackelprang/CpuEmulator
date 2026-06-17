namespace CpuEmulator.Benchmarks.Drivers;

using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Jit;

/// <summary>The 68000 tier driver (Milestone B — the reserved <c>Tiers.cs</c> seam, R1). Constructs a
/// 24-bit BigEndian program <c>AddressSpace</c> (16 MiB backing, R4) + an <c>M68000Cpu</c> (the 68000
/// is memory-mapped — NO separate I/O space, R3), and, for Tier-1, the all-fallback
/// <c>JittedCpu&lt;M68000Cpu&gt;</c> proven byte-identical to the interpreter in M4.6 (PR #46). Every
/// 68000 op falls back to <c>inner.Step</c> in M4, so a green tier-parity baseline measures the generic
/// compiler's dispatch overhead honestly — the "before" the later 68000 hot-op IL emit subtracts from.
/// <para>The 68000 cycle/timing axis is PARTIAL on `main` (M4.5d-2b foundation; the 2b-continuation is
/// deferred, R5): <c>CycleCount</c> is exact for the cycle-exact families but not the whole ISA, so the
/// INSTRUCTION count (Task B2) is the cycle-axis-independent metric the baseline LEADS with; the
/// cycles/sec row carries the timing-axis-coverage caveat (Task B4). The construction mirrors the proven
/// M4.6 / TomHarte path (24-bit BE board, live SR, a sane SSP) so the JIT path is the validated one.</para></summary>
public sealed class M68000TierDriver : ITierDriver
{
    public string Architecture => "m68000";

    public ITierInstance CreateTier0(BenchWorkload w) => Build(w, jit: false, new JitOptions());
    public ITierInstance CreateTier1(BenchWorkload w, JitOptions options) => Build(w, jit: true, options);

    private static ITierInstance Build(BenchWorkload w, bool jit, JitOptions options)
    {
        // The 24-bit BigEndian 68000 board (16 MiB backing) — the exact construction the merged M4.6 /
        // TomHarte runner uses (R4). Unlike the 6502/Z80 (whose board IS their 64 KiB image), the 68000
        // workload carries a SMALL image copied at LoadAddress here (a full 16 MiB byte[] would be
        // wasteful — B3 keeps the image a few words).
        var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        mem.MapMemory(0x000000, new byte[0x1000000], writable: true);
        for (int i = 0; i < w.Image.Length; i++)
            mem.Write8((uint)((w.LoadAddress + i) & 0xFFFFFF), w.Image[i]);

        var cpu = new M68000Cpu(mem);
        cpu.SetRegister("PC", w.StartPc);
        cpu.SetRegister("SR", 0x2700);             // supervisor, interrupts masked — a benign live SR
        cpu.SetRegister("SSP", 0x00FFFC);          // a sane supervisor stack near the top of a small image's RAM
        JittedCpu<M68000Cpu>? j = jit ? new JittedCpu<M68000Cpu>(cpu, M68000Cpu.JitTarget, mem, options: options) : null;
        return new M68000Instance(cpu, j);
    }

    /// <summary>A 68000 tier instance. Tier-0 steps one instruction per advance; Tier-1 runs the
    /// all-fallback JIT a budget-1 block at a time (the M4.6 invariant: one block == one fallback
    /// instruction, confirmed against <c>M68000JitGenericityTests</c>). Neither 68000 workload uses a
    /// host-service boundary (no CP/M-style monitor) — the kernels spin via a back-edge and the
    /// shared <see cref="TierRunner"/> cycle cap terminates them (so <see cref="ParkedThisSlice"/> is
    /// always false, like the Z80-W2 / 6502-W2 capped path). Reports BOTH the cycle count (for
    /// cycles/sec — caveated, B4) and the guest INSTRUCTION count (Task B2 — the cycle-axis-independent
    /// metric the 68000 baseline leads with; one increment per Step / per budget-1 Run).</summary>
    private sealed class M68000Instance(M68000Cpu cpu, JittedCpu<M68000Cpu>? jit) : ITierInstance
    {
        private long _instructions;

        public long CycleCount => cpu.CycleCount;
        public long InstructionCount => _instructions;
        // The 68000 PC is 24-bit; the bench workloads live at a low address (the kernel + its window
        // stay under 0x10000), so the ushort view is exact for the (unused) park check. The 68000
        // workloads are cycle-capped (no success trap), so this never gates a stop.
        public ushort CurrentPc => (ushort)(cpu.GetRegister("PC") & 0xFFFF);
        public bool ParkedThisSlice => false;   // capped workloads: the cap terminates, never a trap-park

        public void AdvanceSlice(long maxCycles)
        {
            // Drive by a budget-1 advance so each instruction is counted (the cycle-axis-independent
            // metric, Task B2). For the all-fallback JIT, one block == one instruction (the M4.6
            // invariant), so a budget-1 Run advances exactly one fallback op — mirroring Step(). A
            // 0-cycle guard catches a diverged op that fails to advance (so the TierRunner loop, which
            // relies on CycleCount climbing toward the cap, can never spin forever).
            long target = cpu.CycleCount + maxCycles;
            while (cpu.CycleCount < target)
            {
                long prevCycles = cpu.CycleCount;
                if (jit is not null) { long budget = 1; jit.Run(ref budget); }
                else cpu.Step();
                _instructions++;
                if (cpu.CycleCount == prevCycles)
                    throw new InvalidOperationException(
                        "m68000: instruction advanced 0 cycles — infinite-loop guard (subject diverged)");
            }
        }
    }
}
