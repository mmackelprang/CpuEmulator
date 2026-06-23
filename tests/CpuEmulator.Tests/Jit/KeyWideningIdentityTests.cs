using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0019 FF-1 SAFE gate: the ushort-&gt;uint block-key widening leaves the non-segmented CPUs
/// (6502/Z80/68000) byte-for-byte unchanged — same blocks compiled, same chain steps, same evictions,
/// same recompiles, same per-Compile fallback emit count. The expected constants are the pre-FF-1
/// baselines captured on this branch @ HEAD~ (== main: the only commit beyond main is the queue claim)
/// over the fixed, fully-deterministic synthetic workloads in <see cref="JitSweepHarness"/> below. The
/// gate FAILS if the widening perturbs any flat-CPU counter — that would flip the change away from SAFE.
///
/// Why synthetic (not Klaus/ZEXDOC): the plan permits a smaller deterministic workload that provably
/// exercises compile + chain + (for the 6502) eviction/recompile, and is byte-deterministic, over a
/// large sweep that can be flaky or fixture-gated ([KlausFact] skips when the binary is absent). These
/// hand-built programs are self-contained (no external fixture), so the gate is stable on any host.</summary>
public class KeyWideningIdentityTests
{
    // ── Captured pre-widening baselines (this branch @ HEAD~ == main; recorded once before Task 3) ──
    // Captured by running JitSweepHarness on the PRE-widening (ushort-key) code; the SAME constants must
    // hold byte-for-byte AFTER the ushort->uint widening. Recorded values (stable across repeated runs):
    //   6502  (two-block chain + SMC thrasher w/ lever OFF): compiles=20 chains=75 evict=16 recompile=13 fallback=0
    //   Z80   (two-block JP/JR chain; canonical LD/HALT probe): compiles=2  chains=3  evict=0  recompile=0 fallback=1
    //   68000 (8 distinct all-fallback NOP blocks, budget 32):  compiles=8  chains=0  evict=0  recompile=0 fallback=1

    [Fact]
    public void Mos6502_jit_sweep_is_byte_identical_after_the_key_widening()
    {
        var r = JitSweepHarness.RunMos6502();
        Assert.Equal(20, r.CompileCount);
        Assert.Equal(75L, r.ChainStepCount);
        Assert.Equal(16L, r.TotalEvictions);
        Assert.Equal(13L, r.TotalRecompiles);
        Assert.Equal(0, r.FallbackEmitCount);
    }

    [Fact]
    public void Z80_jit_sweep_is_byte_identical_after_the_key_widening()
    {
        var r = JitSweepHarness.RunZ80();
        Assert.Equal(2, r.CompileCount);
        Assert.Equal(3L, r.ChainStepCount);
        Assert.Equal(0L, r.TotalEvictions);
        Assert.Equal(0L, r.TotalRecompiles);
        Assert.Equal(1, r.FallbackEmitCount);
    }

    [Fact]
    public void M68000_jit_sweep_is_byte_identical_after_the_key_widening()
    {
        var r = JitSweepHarness.RunM68000();
        Assert.Equal(8, r.CompileCount);
        Assert.Equal(0L, r.ChainStepCount);
        Assert.Equal(0L, r.TotalEvictions);
        Assert.Equal(0L, r.TotalRecompiles);
        Assert.Equal(1, r.FallbackEmitCount);
    }
}
