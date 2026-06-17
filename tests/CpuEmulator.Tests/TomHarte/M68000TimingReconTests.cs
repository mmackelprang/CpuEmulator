using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M4.5d-2b (plan T6): the per-class TIMING-axis reconciliation harness. Runs ONE family file with
/// <c>timingAxis: true</c> — the FULL per-transaction bus-trace diff AND <c>CycleCount == length</c> — over
/// the executable (non-exception) cases. This is the Builder's tight reconcile loop (filter by
/// <c>M68000Timing_&lt;op&gt;</c> = seconds); each family that reconciles is moved into the T9 sweep flip.
///
/// <para>Deferred cases (exception/address-error) are skipped via the runner's DeferredException sentinel —
/// the timing axis only asserts the NON-trap executable cases (the trap-frame timing is T7/T8). The
/// CHK-in-range UNPREDICTABLE-CCR cases are skipped (data-axis artifact, mirrors the 2a sweep).</para>
///
/// <para>Routine/CI runs cap each file at a 200-case sample (CPUEMULATOR_TOMHARTE_SAMPLE); the authoritative
/// substantive/milestone merge gate runs CPUEMULATOR_UAT=full (the full ~8065-case-per-file sweep).</para>
/// </summary>
public abstract class M68000TimingReconBase
{
    protected abstract string VectorFile { get; }

    [M68000TomHarteFact]
    public void Family_is_timing_axis_green()
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, VectorFile);
        if (!File.Exists(path)) return;

        var cases = M68000TomHarteLoader.LoadFile(path);
        Assert.NotEmpty(cases);

        var failures = new List<string>();
        int sampleSize = M68000TomHarteVectors.ResolveSampleSize();
        int run = 0;
        int executed = 0, deferred = 0;
        foreach (var c in cases)
        {
            if (run >= sampleSize) break;
            run++;
            if (IsChkInRangeCase(c)) continue;
            // assertExceptions:true so the runner RUNS the exception cases on the data axis where modeled, but
            // the timing axis here still skips the deferred sentinel (address-error/group-0 frame timing = T8).
            string? r = M68000TomHarteRunner.RunCase(c, timingAxis: true, assertExceptions: true);
            if (ReferenceEquals(r, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (r is not null) { failures.Add(r); if (failures.Count >= 12) break; }
        }

        Assert.True(executed > 0, $"{VectorFile}: 0 executed cases");
        Assert.True(failures.Count == 0,
            $"{VectorFile}: {failures.Count}+ timing-axis failures over {executed} executed ({deferred} deferred):\n"
            + string.Join("\n", failures));
    }

    private static bool IsChkInRangeCase(M68000TomHarteCase c)
    {
        if (c.Initial.Prefetch.Length == 0) return false;
        uint ow = c.Initial.Prefetch[0];
        if ((ow & 0xF1C0u) != 0x4180u) return false;
        return !M68000TomHarteRunner.IsExceptionCase(c);
    }
}

// The families reconciled to FULL cycle-exactness on the timing axis (T6, this PR). Each [Fact] runs the whole
// corpus file with timingAxis:true (the per-transaction trace + CycleCount == length) over its non-deferred
// cases. As later rounds wire the remaining families (the data-dependent .l-register ALU idle, the two-EA MOVE,
// MOVEM, MUL/DIV, the control-transfer reseeds, IPL, address-error), each is added here, then folded into the
// T9 sweep flip when the whole set is green.
//   GREEN (13): the refills-lead register/address-only classes (NOP/SWAP/MOVEQ/LEA), the read-only-EA + RMW
//   single-EA .b/.w ALU (TST.w/CLR.b/NEG.w/CMP.w/CMP.b/AND.b/OR.w/ADD.b/SUB.b).
public sealed class M68000Timing_NOP   : M68000TimingReconBase { protected override string VectorFile => "NOP.json.gz"; }
public sealed class M68000Timing_SWAP  : M68000TimingReconBase { protected override string VectorFile => "SWAP.json.gz"; }
public sealed class M68000Timing_MOVEq : M68000TimingReconBase { protected override string VectorFile => "MOVE.q.json.gz"; }
public sealed class M68000Timing_LEA   : M68000TimingReconBase { protected override string VectorFile => "LEA.json.gz"; }
public sealed class M68000Timing_TST_w : M68000TimingReconBase { protected override string VectorFile => "TST.w.json.gz"; }
public sealed class M68000Timing_CLR_b : M68000TimingReconBase { protected override string VectorFile => "CLR.b.json.gz"; }
public sealed class M68000Timing_NEG_w : M68000TimingReconBase { protected override string VectorFile => "NEG.w.json.gz"; }
public sealed class M68000Timing_CMP_w : M68000TimingReconBase { protected override string VectorFile => "CMP.w.json.gz"; }
public sealed class M68000Timing_CMP_b : M68000TimingReconBase { protected override string VectorFile => "CMP.b.json.gz"; }
public sealed class M68000Timing_AND_b : M68000TimingReconBase { protected override string VectorFile => "AND.b.json.gz"; }
public sealed class M68000Timing_OR_w  : M68000TimingReconBase { protected override string VectorFile => "OR.w.json.gz"; }
public sealed class M68000Timing_ADD_b : M68000TimingReconBase { protected override string VectorFile => "ADD.b.json.gz"; }
public sealed class M68000Timing_SUB_b : M68000TimingReconBase { protected override string VectorFile => "SUB.b.json.gz"; }
