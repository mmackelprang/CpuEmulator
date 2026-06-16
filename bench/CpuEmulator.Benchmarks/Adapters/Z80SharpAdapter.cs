using System.Diagnostics;

namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>Z80dotNet (Konamiman's cycle-accurate C# Z80, the NuGet package) — the in-process
/// third-party Z80 subject (no toolchain, no subprocess: the cross-language C# anchor, mirroring
/// <see cref="Asm6502Adapter"/> for the 6502). Probes for the Z80dotNet assembly being loadable (it
/// is when the package restored). Built behind the HAS_Z80SHARP compile constant so the bench library
/// still BUILDS when the package is unavailable offline (<c>-p:UseZ80Sharp=false</c>) — the adapter
/// then reports "package not referenced" and skips.
/// <para>It runs the SAME portable Z80 workload images our two tiers run: Z80-W2 (the arithmetic
/// kernel) to a bounded T-state window, and Z80-W1 (the ZEXDOC prefix) servicing the CP/M BDOS CALL
/// host-side the same way the driver + <c>CpmBdosHost</c> do (fn-2/fn-9 + host RET), so it executes
/// the identical real ZEX code. A run that under/overshoots the requested window is reported
/// Ran=false ("diverged/stalled"), never a fast-but-wrong number — the existing honesty mechanism.</para></summary>
public sealed class Z80SharpAdapter : IEmulatorAdapter
{
    private const long Z80SharpMeasureCycles = 20_000_000;   // a bounded T-state window (cycles/sec is a rate)

    public string Name => "Z80dotNet (C#)";

#if HAS_Z80SHARP
    public bool Probe(out string reason)
    {
        try
        {
            // Touch the type so a missing/incompatible assembly surfaces here, not in Measure.
            _ = typeof(global::Konamiman.Z80dotNet.Z80Processor);
            var v = typeof(global::Konamiman.Z80dotNet.Z80Processor).Assembly.GetName().Version;
            reason = $"Z80dotNet {v}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Z80dotNet assembly not loadable: {ex.Message}";
            return false;
        }
    }

    public AdapterResult Measure(BenchWorkload workload)
    {
        try
        {
            var v = typeof(global::Konamiman.Z80dotNet.Z80Processor).Assembly.GetName().Version;
            string note = $"Z80dotNet {v}";

            // A bounded T-state window on both workloads (cap mode) — consistent with the other
            // subjects (cycles/sec is a rate over a representative slice). For W1 the window is
            // min(the per-subject measure cap, the committed window); for W2 it is min(cap, the kernel cap).
            long cap = workload.FixedCycleCap is long c
                ? Math.Min(Z80SharpMeasureCycles, c)
                : Math.Min(Z80SharpMeasureCycles, workload.ExpectedCycles);

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
                    $"subject ran only {cycles} of {cap} requested T-states (jammed/stalled/diverged)");
            return AdapterResult.Measured(cycles, sw.Elapsed.TotalSeconds, note);
        }
        catch (Exception ex)
        {
            return AdapterResult.Skipped($"adapter error: {ex.Message}");
        }
    }

    private const ushort WarmBoot = 0x0000;   // CP/M warm-boot sentinel (W1 early-stop)
    private const ushort BdosEntry = 0x0005;  // the CALL target the BDOS intercept fires on (W1)

    /// <summary>Run the workload on Z80dotNet for a bounded window of <paramref name="cap"/> emulated
    /// T-states (cap mode; cycles/sec is a rate). For the BDOS workload (W1) it services the CP/M BDOS
    /// CALL host-side (fn-2/fn-9 + RET) the same way the driver + CpmBdosHost do, so it runs the
    /// identical ZEX code; it stops early on the warm-boot sentinel. Outputs the T-states actually
    /// run.</summary>
    private static void RunOnce(BenchWorkload w, long cap, out long cycles)
    {
        var cpu = new global::Konamiman.Z80dotNet.Z80Processor();
        // The Z80dotNet step uses its own memory + the InstructionExecutionContext; give it a flat
        // 64 KiB RAM seeded with the workload image. Auto-stops disabled (we drive the window).
        var mem = new global::Konamiman.Z80dotNet.PlainMemory(0x10000);
        mem.SetContents(0, (byte[])w.Image.Clone(), 0, null);
        cpu.Memory = mem;
        cpu.AutoStopOnDiPlusHalt = false;
        cpu.AutoStopOnRetWithStackEmpty = false;

        cpu.Reset();
        cpu.Registers.PC = w.StartPc;
        cpu.Registers.SP = unchecked((short)0xFFFE);

        ulong start = cpu.TStatesElapsedSinceReset;
        while (cpu.TStatesElapsedSinceReset - start < (ulong)cap)
        {
            ushort pc = cpu.Registers.PC;
            if (w.UsesCpmBdos)
            {
                if (pc == WarmBoot) break;                       // early-stop (rare for a capped prefix)
                if (pc == BdosEntry) { ServiceBdos(cpu); continue; }
            }
            cpu.ExecuteNextInstruction();
        }
        cycles = (long)(cpu.TStatesElapsedSinceReset - start);
    }

    /// <summary>Service a BDOS call host-side: fn-2 (console out, char in E) + fn-9 ($-string at DE),
    /// then host RET — discarded console output (throughput run). A port of the proven
    /// CpmBdosHost.ServiceBdos convention onto Z80dotNet's register/memory surface.</summary>
    private static void ServiceBdos(global::Konamiman.Z80dotNet.Z80Processor cpu)
    {
        byte fn = cpu.Registers.C;
        switch (fn)
        {
            case 2: // console out: char in E — discarded
                _ = cpu.Registers.E;
                break;
            case 9: // print $-terminated string at DE — discarded
            {
                ushort addr = unchecked((ushort)cpu.Registers.DE);
                for (int guard = 0; guard < 0x10000; guard++)
                {
                    byte b = cpu.Memory[addr];
                    if (b == (byte)'$') break;
                    addr = unchecked((ushort)(addr + 1));
                }
                break;
            }
            default:
                break; // unimplemented BDOS function — silent RET (ZEX never hits this)
        }
        // Host RET: pop the 16-bit return address the CALL pushed and set PC.
        ushort sp = unchecked((ushort)cpu.Registers.SP);
        byte lo = cpu.Memory[sp];
        byte hi = cpu.Memory[unchecked((ushort)(sp + 1))];
        cpu.Registers.SP = unchecked((short)(sp + 2));
        cpu.Registers.PC = unchecked((ushort)((hi << 8) | lo));
    }
#else
    public bool Probe(out string reason)
    {
        reason = "Z80dotNet package not referenced (built with -p:UseZ80Sharp=false); "
               + "rebuild the bench with UseZ80Sharp=true + nuget.org reachable to populate this row";
        return false;
    }

    public AdapterResult Measure(BenchWorkload workload) =>
        AdapterResult.Skipped("Z80dotNet package not referenced");
#endif
}
