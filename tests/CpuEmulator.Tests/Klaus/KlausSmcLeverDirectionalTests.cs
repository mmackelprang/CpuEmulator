using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.Klaus;

/// <summary>M6 PR-S — the BOUNDED, DIRECTIONAL W1 (Klaus) check (ADR 0011 §3.4 / the PR-S benchmark
/// exception). The full W1/W2/W3 throughput capture is the arc-end benchmark's job; this is the
/// directional confirmation that the SMC/recompile-cost lever WORKS on the actual SMC-heavy workload:
/// over a BOUNDED Klaus window, the lever-ON recompile count collapses vs lever-OFF (the per-dispatch
/// thrash the lever exists to kill), and the lever-ON wall-clock is no worse. Bounded (a few-million-
/// cycle window, not the 96M headline run) + foreground. Skips when the Klaus binary is absent.</summary>
public class KlausSmcLeverDirectionalTests(ITestOutputHelper output)
{
    private const ushort StartAddress = 0x0400;
    private const long Window = 5_000_000;   // bounded: ~5M cycles, a few seconds — NOT the full 96M run

    private static (Mos6502Cpu Cpu, JittedCpu<Mos6502Cpu> Jit) NewKlausJit(byte[] image, JitOptions opts)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, (byte[])image.Clone(), writable: true);   // Klaus self-modifies RAM
        var cpu = new Mos6502Cpu(space) { PC = StartAddress, S = 0xFD, P = 0x34 };
        return (cpu, new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space, options: opts));
    }

    private static (long Recompiles, long Evictions, int HotPcs, double Seconds) RunWindow(byte[] image, JitOptions opts)
    {
        var (cpu, jit) = NewKlausJit(image, opts);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (cpu.CycleCount < Window)
        {
            long budget = System.Math.Min(2_000_000, Window - cpu.CycleCount);
            jit.Run(ref budget);
        }
        sw.Stop();
        return (jit.TotalRecompiles, jit.TotalEvictions, jit.SmcHotPcCount, sw.Elapsed.TotalSeconds);
    }

    [KlausJitFact]
    public void Lever_collapses_Klaus_recompiles_over_a_bounded_window()
    {
        byte[] image = File.ReadAllBytes(KlausVectors.TryGetBinaryPath()!);
        Assert.Equal(0x10000, image.Length);

        var off = RunWindow(image, new JitOptions { DisableSmcLever = true });  // pre-PR-S thrash
        var on  = RunWindow(image, new JitOptions());                            // lever ON (the fix)

        output.WriteLine($"Klaus[{Window} cyc]  lever OFF: recompiles={off.Recompiles:N0} " +
                         $"evictions={off.Evictions:N0} {off.Seconds:F2}s");
        output.WriteLine($"Klaus[{Window} cyc]  lever ON : recompiles={on.Recompiles:N0} " +
                         $"evictions={on.Evictions:N0} hotPCs={on.HotPcs} {on.Seconds:F2}s");

        // DIRECTIONAL gate 1: Klaus thrashes WITHOUT the lever (recompiles dominate the window).
        Assert.True(off.Recompiles > 1000,
            $"Klaus should thrash without the lever; recompiles={off.Recompiles}");
        // DIRECTIONAL gate 2: the lever SHARPLY drops recompiles (the W1 0.00× mechanism removed).
        Assert.True(on.Recompiles * 4 < off.Recompiles,
            $"lever ON recompiles {on.Recompiles} should be << OFF {off.Recompiles}");
        // DIRECTIONAL gate 3: the lever actually fired on Klaus (>= 1 SMC-hot PC).
        Assert.True(on.HotPcs >= 1, "the lever should mark >= 1 Klaus PC SMC-hot");
        // DIRECTIONAL gate 4: lever-ON wall-clock is no worse than lever-OFF over the window (the whole
        // point — recompiling less is faster). A generous 1.5x slack absorbs machine noise on a bounded
        // window; the headline magnitude lives in the arc-end full-W1 capture, not this pin.
        Assert.True(on.Seconds <= off.Seconds * 1.5,
            $"lever ON {on.Seconds:F2}s should not be slower than OFF {off.Seconds:F2}s");
    }
}
