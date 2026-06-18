namespace CpuEmulator.Benchmarks.Drivers;

using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;

/// <summary>The 8086 tier driver (M6 PR-A — the §0.3 measurement enablement). Constructs a 20-bit
/// little-endian program AddressSpace (1 MiB backing) + an M8086Cpu (ONE bus — the 8086 has no separate
/// I/O space in this core; IN/OUT are interpreter-internal open-bus, ADR 0011 §0.2), and, for Tier-1, the
/// all-fallback JittedCpu&lt;M8086Cpu&gt; proven byte-identical to the interpreter through M5.6 (TomHarte
/// green). Every 8086 op falls back to inner.Step in M5, so a green tier-parity baseline measures the
/// generic compiler's dispatch overhead honestly — the "before" the later 8086 hot-op IL emit subtracts
/// from. The 8086 cycle model is rudimentary on main (ReadBus/WriteBus charge 1 each; the timing axis is
/// post-M5), so the INSTRUCTION count is the cycle-axis-independent metric this baseline leads with (exactly
/// the 68000's instructions/sec lead, M6 plan B2); the cycles/sec row carries the partial-axis caveat (A3).
/// The image is loaded at CS:0 with IP = StartPc; CS/DS/SS/ES are seeded to 0 so a low-loaded flat image
/// runs without segmentation surprises (BenchWorkload has no segment field, A10 — the driver pins them).</summary>
public sealed class M8086TierDriver : ITierDriver
{
    public string Architecture => "m8086";

    public ITierInstance CreateTier0(BenchWorkload w) => Build(w, jit: false, new JitOptions());
    public ITierInstance CreateTier1(BenchWorkload w, JitOptions options) => Build(w, jit: true, options);

    private static ITierInstance Build(BenchWorkload w, bool jit, JitOptions options)
    {
        var mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);   // little-endian default
        mem.MapMemory(0x00000, new byte[0x100000], writable: true);             // full 1 MiB
        for (int i = 0; i < w.Image.Length; i++)
            mem.Write8((uint)((w.LoadAddress + i) & 0xFFFFF), w.Image[i]);

        var cpu = new M8086Cpu(mem);
        cpu.SetRegister("CS", 0x0000);              // flat: CS=0 so the physical fetch is (0<<4)+IP = IP
        cpu.SetRegister("DS", 0x0000);
        cpu.SetRegister("SS", 0x0000);
        cpu.SetRegister("ES", 0x0000);
        cpu.SetRegister("IP", w.StartPc);           // the 8086 program counter is IP (A3)
        cpu.SetRegister("SP", 0xFFFE);              // a sane stack near the top of the flat segment
        cpu.SetRegister("FLAGS", 0x0002);           // bit 1 is the reserved-always-1 8086 FLAGS bit
        JittedCpu<M8086Cpu>? j = jit ? new JittedCpu<M8086Cpu>(cpu, M8086Cpu.JitTarget, mem, options: options) : null;
        return new M8086Instance(cpu, j);
    }

    /// <summary>An 8086 tier instance. Tier-0 steps; Tier-1 runs the all-fallback JIT a slice at a time
    /// (budget-1 == one instruction per block, the M5 all-fallback invariant — M8088TomHarteRunner:151-152).
    /// Stop is the FIXED CYCLE/INSTRUCTION CAP for every workload (no host-service boundary — the kernels
    /// spin via a back-edge and the cap terminates). Reports BOTH the cycle count and the instruction count;
    /// the 8086 baseline leads with instructions/sec (the cycle axis is partial, A5).</summary>
    private sealed class M8086Instance(M8086Cpu cpu, JittedCpu<M8086Cpu>? jit) : ITierInstance
    {
        private long _instructions;

        public long CycleCount => cpu.CycleCount;
        public long InstructionCount => _instructions;
        public ushort CurrentPc => (ushort)(cpu.GetRegister("IP") & 0xFFFF);   // IP, not PC (A3)
        public bool ParkedThisSlice => false;   // capped workloads: the cap terminates, never a trap-park

        public void AdvanceSlice(long maxCycles)
        {
            long target = cpu.CycleCount + maxCycles;
            while (cpu.CycleCount < target)
            {
                long prevCycles = cpu.CycleCount;
                if (jit is not null) { long budget = 1; jit.Run(ref budget); }
                else cpu.Step();
                _instructions++;
                if (cpu.CycleCount == prevCycles)
                    throw new InvalidOperationException(
                        "m8086: instruction advanced 0 cycles — infinite-loop guard (subject diverged)");
            }
        }
    }
}
