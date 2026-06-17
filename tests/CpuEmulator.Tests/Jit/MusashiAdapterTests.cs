using CpuEmulator.Benchmarks;
using CpuEmulator.Benchmarks.Adapters;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M4a — smoke test for the Musashi 68000 HEAD-TO-HEAD reference adapter. Pins the
/// graceful-degradation seam (the load-bearing invariant: an absent toolchain/source degrades exactly
/// this one row, never a crash) AND the measured head-to-head path when the toolchain IS present:
/// <list type="bullet">
/// <item>Probe() NEVER throws — it returns true (compiler + Musashi source present) or false-with-note.</item>
/// <item>When Probe()==true, Measure runs a TINY 68000 window and returns Ran==true with a positive
/// guest InstructionsPerSecond (the 68000 leads with instructions/sec — its cycle axis is partial).</item>
/// </list>
/// The measured-path assertion is gated on Probe()==true so the test passes on a box without a C
/// compiler / without the fetched Musashi source (it then asserts only the skip-with-note contract).
/// A TINY instruction window keeps the suite fast (the committed 50M window is the runner's job).</summary>
public class MusashiAdapterTests
{
    [Fact]
    public void Probe_never_throws_and_either_runs_head_to_head_or_skips_with_a_note()
    {
        var adapter = new MusashiAdapter();

        bool present;
        string reason = "";
        var probeEx = Record.Exception(() => present = adapter.Probe(out reason));
        Assert.Null(probeEx);   // Probe MUST NOT throw (the graceful-degradation contract)
        present = adapter.Probe(out reason);

        Assert.Equal("Musashi (C)", adapter.Name);   // must match the cited registry subject exactly

        if (!present)
        {
            // Absent toolchain/source: a skip-with-note — the cited placeholder stays in place.
            Assert.False(string.IsNullOrWhiteSpace(reason), "an absent subject must carry a skip reason");
            return;
        }

        // Present: compile-once-cached (incl. the m68kmake codegen step) + run a TINY 68000 window.
        // A small instruction budget keeps this fast; the workload bytes are the FROZEN Sieve image.
        var tiny = M68000Workloads.SieveKernel() with
        {
            FixedCycleCap = 200_000,
            ExpectedCycles = 200_000,
        };

        AdapterResult result = default;
        var measureEx = Record.Exception(() => result = adapter.Measure(tiny));
        Assert.Null(measureEx);   // Measure MUST NOT throw either

        Assert.True(result.Ran, $"Musashi should run head-to-head when probed present: {result.Note}");
        Assert.True(result.CyclesPerSecond > 0, $"expected a positive cycles/sec: {result}");
        // The B2 seam: the 68000 head-to-head row carries guest instructions/sec (its trustworthy,
        // cross-CPU-comparable headline) — proving the INSTRUCTIONS line flows through SubprocessRunner.
        Assert.True(result.InstructionsPerSecond > 0,
            $"the Musashi head-to-head row must report instructions/sec (guest-MIPS): {result}");
    }
}
