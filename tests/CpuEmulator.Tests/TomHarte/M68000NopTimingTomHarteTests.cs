using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M4.5d-2b (plan T5): the bus-transaction + idle-cycle MACHINERY proof. Runs the NOP corpus file with
/// <c>timingAxis: true</c> — the FULL per-transaction bus-trace diff AND <c>CycleCount == length</c> — to prove
/// the structured per-transaction model end-to-end on a register-only class.
///
/// <para><b>Why NOP is the right T5 witness.</b> NOP (length 4) is a single transaction
/// <c>["r", 4, 6, 0xC04, ".w", refillword]</c>: the operword came from the SEEDED prefetch queue (already in
/// <c>q0</c>, NOT re-read), so the ONLY traced access is the prefetch REFILL read at the frontier (formalPc+4 =
/// 0xC04). It exercises every piece of the T5 machinery:</para>
/// <list type="bullet">
/// <item>the <b>untraced seed</b> (M68000FetchStream.SeedPeek via IAddressSpace.TryPeek8) — proven by the trace
/// being EXACTLY one access (if the seed reads leaked, the trace would carry two phantom fetches at PC/PC+2);</item>
/// <item>the <b>refill cycle charge</b> (<c>4 * RefillCount</c>) — proven by <c>CycleCount == 4</c>; and</item>
/// <item>the <b>idle primitive</b> (IdleCycles flushing _pendingIdle == 0 for a register-only class) — proven by
/// there being NO extra idle cycles inflating the count past 4.</item>
/// </list>
///
/// <para><b>Scope (T5 honesty).</b> This is a LOCAL proof of the machinery, NOT the 2a big sweep with timingAxis
/// flipped on (that is T9, done by the parent). The per-class cycle reconciliation for the operand/idle-bearing
/// classes is T6. NOP is the one class T5 makes fully cycle-exact.</para>
/// </summary>
public class M68000NopTimingTomHarteTests
{
    [M68000TomHarteFact]
    public void Nop_is_timing_axis_green_trace_and_cycle_exact()
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, "NOP.json.gz");
        if (!File.Exists(path)) return;   // trimmed cache — skip silently (the Fact attr skips when the dir is absent)

        var cases = M68000TomHarteLoader.LoadFile(path);
        Assert.NotEmpty(cases);

        var failures = new List<string>();
        int executed = 0;
        foreach (var c in cases)
        {
            // NOP never takes an exception; the full timing axis (trace + CycleCount == length) is asserted.
            string? r = M68000TomHarteRunner.RunCase(c, timingAxis: true);
            executed++;
            if (r is not null) { failures.Add(r); if (failures.Count >= 10) break; }
        }

        Assert.True(executed > 0, "NOP.json.gz: 0 executed cases — the timing proof would be vacuous");
        Assert.True(failures.Count == 0,
            $"NOP.json.gz: {failures.Count}+ timing-axis failures over {executed} executed:\n" +
            string.Join("\n", failures));
    }
}
