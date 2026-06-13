using System.Diagnostics;

namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>Asm6502 (a cycle-accurate C# 6502, the NuGet package) — the in-process third-party
/// subject (no toolchain, no subprocess: the most likely row to populate). Probes for the Asm6502
/// assembly being loadable (it is when the package restored). Built behind the HAS_ASM6502 compile
/// constant so the bench library still BUILDS when the package is unavailable offline
/// (<c>-p:UseAsm6502=false</c>) — the adapter then reports "package not referenced" and skips.</summary>
public sealed class Asm6502Adapter : IEmulatorAdapter
{
    private const long Asm6502W2MeasureCycles = 20_000_000;

    public string Name => "Asm6502 (C#)";

#if HAS_ASM6502
    public bool Probe(out string reason)
    {
        try
        {
            // Touch the type so a missing/incompatible assembly surfaces here, not in Measure.
            _ = typeof(global::Asm6502.Mos6502Cpu);
            var v = typeof(global::Asm6502.Mos6502Cpu).Assembly.GetName().Version;
            reason = $"Asm6502 {v}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Asm6502 assembly not loadable: {ex.Message}";
            return false;
        }
    }

    public AdapterResult Measure(BenchWorkload workload)
    {
        try
        {
            var v = typeof(global::Asm6502.Mos6502Cpu).Assembly.GetName().Version;
            string note = $"Asm6502 {v}";

            // A bounded cycle window on both workloads (cap mode) — consistent with the other
            // third-party subjects (cycles/sec is a rate over a representative slice). For W1 the
            // window is min(the per-subject measure cap, the anchor); for W2 it is the kernel cap.
            long cap = workload.FixedCycleCap is long c
                ? Math.Min(Asm6502W2MeasureCycles, c)
                : Math.Min(Asm6502W2MeasureCycles, workload.ExpectedCycles);

            // Warmup (excluded): a short slice so RyuJIT + the package's dispatch are hot.
            RunOnce(workload, Math.Min(cap, 1_000_000), out _);

            var sw = Stopwatch.StartNew();
            RunOnce(workload, cap, out long cycles);
            sw.Stop();

            // Divergence gate (the honesty mechanism): a subject that jammed/parked/stalled runs far
            // fewer cycles than the requested window. Require it completed most of the window; else
            // report Ran=false ("diverged/stalled"), never a fast-but-wrong number.
            if (cycles < cap / 2)
                return AdapterResult.Skipped(
                    $"subject ran only {cycles} of {cap} requested cycles (jammed/stalled/diverged)");
            return AdapterResult.Measured(cycles, sw.Elapsed.TotalSeconds, note);
        }
        catch (Exception ex)
        {
            return AdapterResult.Skipped($"adapter error: {ex.Message}");
        }
    }

    /// <summary>Run the workload on Asm6502 for a bounded window of <paramref name="cap"/> emulated
    /// cycles (cap mode — consistent with the other third-party subjects; cycles/sec is a rate). Stops
    /// early only if the CPU jams/halts (a hard stall). Outputs the cycles actually run.</summary>
    private static void RunOnce(BenchWorkload w, long cap, out long cycles)
    {
        var bus = new ArrayBus(w.Image);
        var cpu = new global::Asm6502.Mos6502Cpu(bus)
        {
            PC = w.StartPc,
            S = 0xFD,
        };
        cpu.A = 0; cpu.X = 0; cpu.Y = 0;
        cpu.SR = (global::Asm6502.Mos6502CpuFlags)0x34;

        ulong startTicks = cpu.TimestampCounter;
        while (cpu.TimestampCounter - startTicks < (ulong)cap)
        {
            cpu.Step();                          // cycle-accurate single instruction
            if (cpu.IsJammed || cpu.IsHalted) break;
        }
        cycles = (long)(cpu.TimestampCounter - startTicks);
    }

    /// <summary>A flat 64 KiB RAM bus for Asm6502's IMos6502CpuMemoryBus.</summary>
    private sealed class ArrayBus(byte[] image) : global::Asm6502.IMos6502CpuMemoryBus
    {
        private readonly byte[] _ram = (byte[])image.Clone();
        public byte Read(ushort address) => _ram[address];
        public void Write(ushort address, byte value) => _ram[address] = value;
        public void Trace(global::Asm6502.Mos6502MemoryBusAccessKind kind) { /* no tracing in the bench */ }
    }
#else
    public bool Probe(out string reason)
    {
        reason = "Asm6502 package not referenced (built with -p:UseAsm6502=false); "
               + "rebuild the bench with UseAsm6502=true + nuget.org reachable to populate this row";
        return false;
    }

    public AdapterResult Measure(BenchWorkload workload) =>
        AdapterResult.Skipped("Asm6502 package not referenced");
#endif
}
