using System.Diagnostics;
using CpuEmulator.Core;

namespace CpuEmulator.Surface.Web;

/// <summary>Pushes the <c>PF</c> performance/telemetry frame to a sink UNCONDITIONALLY on every
/// <see cref="Tick"/> (design handoff 2026-06-23-perf-overlay §6.3) — the deliberate opposite of
/// <see cref="StatusPusher"/>, which dedupes on byte-equal snapshots. Perf rates always move, so there is
/// nothing to dedupe; a tiny JSON a few times a second is negligible. The pusher OWNS the cycles/sec rate
/// computation (§7 item 3): it keeps the previous (CycleCount, timestamp) and divides ΔCycleCount by the
/// elapsed wall seconds each tick — no rate API exists on the CPU, so the producer derives it here. The
/// sample window is the wall-time between consecutive ticks. The HUD's caller drives <see cref="Tick"/> at
/// its own ~3 Hz beat (separate from the ~60 Hz frame pump). All reads are introspection-only — the pusher
/// never steps or mutates the machine.</summary>
public sealed class PerfPusher
{
    private readonly Machine _machine;
    private readonly Func<string> _boardName;
    private readonly Action<byte[]> _sink;
    private readonly Func<long> _hostWorkingSetBytes;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _prevCycleCount;
    private double _prevSeconds;
    private bool _primed;

    public PerfPusher(Machine machine, Func<string> boardName, Action<byte[]> sink,
                      Func<long>? hostWorkingSetBytes = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(boardName);
        ArgumentNullException.ThrowIfNull(sink);
        _machine = machine;
        _boardName = boardName;
        _sink = sink;
        // Default: the real process working set. Injectable so a test can read a real-but-deterministic value
        // without a live process (the host-memory number is server-side only; never a guest concern).
        _hostWorkingSetBytes = hostWorkingSetBytes
            ?? (static () => Process.GetCurrentProcess().WorkingSet64);
    }

    /// <summary>Sample the live machine + host once, compute cycles/sec over the window since the last
    /// tick, encode a <c>PF</c> frame, and push it unconditionally. The FIRST tick primes the rate baseline
    /// (cycles/sec = 0 — truthful: no window has elapsed yet) and still pushes, so the HUD leaves its
    /// "initializing" em-dashes the moment it opens.</summary>
    public void Tick()
    {
        long cycleCount = _machine.Cpu.CycleCount;
        double seconds = _clock.Elapsed.TotalSeconds;

        double cyclesPerSecond = 0.0;
        if (_primed)
        {
            double dt = seconds - _prevSeconds;
            if (dt > 0)
                cyclesPerSecond = (cycleCount - _prevCycleCount) / dt;
        }
        // Defensive: never put a non-finite rate on the wire. The dt>0 guard already prevents a divide,
        // but a future non-monotonic CycleCount (e.g. a mid-session machine reset that zeroes the counter)
        // could otherwise yield NaN/±Inf — and System.Text.Json THROWS on those by default, which would
        // tear down the WS session (the opposite of the §8 calm-degenerate contract). Fall back to 0.
        if (!double.IsFinite(cyclesPerSecond))
            cyclesPerSecond = 0.0;
        _prevCycleCount = cycleCount;
        _prevSeconds = seconds;
        _primed = true;

        JitStats? jit = _machine.JitMetrics is { } m
            ? new JitStats(m.CompileCount, m.TotalRecompiles, m.TotalEvictions, m.SmcHotPcCount)
            : null;

        CoprocessorStatus? cpu2 = _machine.Coprocessor is { } copro
            ? new CoprocessorStatus(copro.Architecture, _machine.CoprocessorActive)
            : null;

        var stats = new PerfStats(
            Board: _boardName(),
            CyclesPerSecond: cyclesPerSecond,
            NominalClockHz: _machine.NominalClockHz,
            RamBytes: _machine.AddressSpaceBytes,
            HostWorkingSetBytes: _hostWorkingSetBytes(),
            IsJitted: _machine.IsJitted,
            Jit: jit,
            Coprocessor: cpu2);

        _sink(FrameCodec.EncodePerf(stats));
    }
}
