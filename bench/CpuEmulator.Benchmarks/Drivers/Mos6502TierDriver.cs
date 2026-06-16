namespace CpuEmulator.Benchmarks.Drivers;

using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

/// <summary>The 6502 tier driver: it reproduces the ORIGINAL <see cref="TierRunner"/> 6502
/// construction byte-for-byte so the committed 6502 cycle counts (W1 96,241,367 / W2 50,000,001 on
/// both tiers) do not move. Same <c>AddressSpace(Program, 16)</c>, same
/// <c>MapMemory(LoadAddress, Image.Clone(), writable:true)</c>, same
/// <c>new Mos6502Cpu(space, Nop){ PC=StartPc, S=0xFD, P=0x34 }</c>, same
/// <c>JittedCpu&lt;Mos6502Cpu&gt;</c> ctor, same per-Step (interp) / per-BulkSlice (JIT) parked-trap
/// detection. The BulkSlice budget + the <c>VerifyTrap</c> divergence throw live in the shared
/// <see cref="TierRunner"/> loop; this driver supplies only the seeded instances + their slice
/// granularity + the park condition.</summary>
public sealed class Mos6502TierDriver : ITierDriver
{
    public string Architecture => "mos6502";

    public ITierInstance CreateTier0(BenchWorkload w)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
        var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };
        return new InterpInstance(cpu);
    }

    public ITierInstance CreateTier1(BenchWorkload w, JitOptions options)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(w.LoadAddress, (byte[])w.Image.Clone(), writable: true);
        var cpu = new Mos6502Cpu(space, UndefinedOpcodePolicy.Nop) { PC = w.StartPc, S = 0xFD, P = 0x34 };
        var jit = new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space, options: options);
        long target = w.FixedCycleCap ?? w.ExpectedCycles;
        return new JitInstance(cpu, jit, target);
    }

    /// <summary>Tier-0: ONE Step per slice (the original RunInterpreter granularity). Parked =
    /// PC unchanged across the Step — the unconditional park the original used (it has no
    /// below-target guard, so a Step that lands on the success trap exactly AT the anchor still
    /// triggers VerifyTrap).</summary>
    private sealed class InterpInstance(Mos6502Cpu cpu) : ITierInstance
    {
        private bool _parked;

        public long CycleCount => cpu.CycleCount;
        public ushort CurrentPc => cpu.PC;
        public bool ParkedThisSlice => _parked;

        public void AdvanceSlice(long maxCycles)
        {
            ushort before = cpu.PC;
            cpu.Step();
            _parked = cpu.PC == before;
        }
    }

    /// <summary>Tier-1: ONE BulkSlice-bounded Run per slice (the original RunJit granularity).
    /// Parked = PC unchanged across the slice AND CycleCount still below target — the exact
    /// condition RunJit used (the below-target guard distinguishes a real trap-park from the W2
    /// cap simply exhausting the budget at the same PC).</summary>
    private sealed class JitInstance(Mos6502Cpu cpu, JittedCpu<Mos6502Cpu> jit, long target) : ITierInstance
    {
        private bool _parked;

        public long CycleCount => cpu.CycleCount;
        public ushort CurrentPc => cpu.PC;
        public bool ParkedThisSlice => _parked;

        public void AdvanceSlice(long maxCycles)
        {
            ushort before = cpu.PC;
            long budget = maxCycles;
            jit.Run(ref budget);
            _parked = cpu.PC == before && cpu.CycleCount < target;
        }
    }
}
