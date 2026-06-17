using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M4.5a: the MOVE-family TomHarte green sweep — the un-fakeable, silicon-derived ground-truth gate. Runs
/// EVERY case of the 10 in-scope MOVE-family files through the real Step+diff runner and asserts the DATA axis
/// (final D0–D7, A0–A6, USP, SSP, SR, RAM) byte-exact — the pure MOVE execution result. The TIMING axis
/// (final.pc, final.prefetch, the per-transaction bus trace, the cycle count) is the prefetch-queue's
/// observable state and is M4.5d per ADR 0004 §3.
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> Each in-scope file gets its OWN derived class
/// (hence its own xUnit collection) so the 10 files distribute across cores instead of running serially in one
/// theory class. The assertion body is IDENTICAL to the pre-split single-theory body — only the collection
/// boundary changed. <see cref="CanonicalFiles"/> is the source of truth; the coverage guard
/// (<see cref="M68000MoveTomHarteCoverageGuard"/>) asserts the derived classes cover it exactly.</para>
///
/// EXCLUDED (out of M4.5a scope): MOVE.q (MOVEQ → M4.5b/c), MOVEM.w/.l + MOVEP.w/.l (system-misc → M4.5c).
/// Skip-when-absent (vector-less environments) but MUST run green with the vectors PRESENT for merge — a skip is
/// not a mergeable state.
///
/// <para>Routine/CI runs cap each file at a 200-case sample (CPUEMULATOR_TOMHARTE_SAMPLE); the authoritative
/// substantive/milestone merge gate runs CPUEMULATOR_UAT=full (the full ~8065-case-per-file sweep).</para>
/// </summary>
public abstract class M68000MoveTomHarteSweepBase
{
    /// <summary>ALL 10 in-scope MOVE families — DATA axis. The source of truth for the coverage guard.</summary>
    public static readonly string[] CanonicalFiles =
    [
        "MOVE.b.json.gz", "MOVE.w.json.gz", "MOVE.l.json.gz",
        "MOVEA.w.json.gz", "MOVEA.l.json.gz",
        "MOVEfromSR.json.gz", "MOVEtoSR.json.gz", "MOVEtoCCR.json.gz",
        "MOVEfromUSP.json.gz", "MOVEtoUSP.json.gz",
    ];

    /// <summary>The vector file this sweep class asserts (one file == one collection == one parallel unit).</summary>
    protected abstract string VectorFile { get; }

    /// <summary>Public accessor for the coverage guard (the protected member is not reflectable cross-type).</summary>
    public string FileForGuard => VectorFile;

    [M68000TomHarteFact]
    public void Move_family_data_axis_is_TomHarte_green() => RunSweep(VectorFile, timingAxis: false);

    private static void RunSweep(string file, bool timingAxis)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);   // the test is skipped at discovery when vectors are absent; present == not null
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"in-scope MOVE-family vector file missing: {path}");

        var cases = M68000TomHarteLoader.LoadFile(path);
        Assert.NotEmpty(cases);   // a present-but-empty file would silently pass — guard it

        var failures = new List<string>();
        int sampleSize = M68000TomHarteVectors.ResolveSampleSize();
        int run = 0;
        int executed = 0;   // non-exception MOVE cases actually run + asserted on the data (or full) axis
        int deferred = 0;   // exception cases (M4.5d) — counted, not asserted (would be a drift false-positive)
        foreach (var c in cases)
        {
            if (run >= sampleSize) break;
            run++;
            string? r = M68000TomHarteRunner.RunCase(c, timingAxis);
            if (ReferenceEquals(r, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (r is not null)
            {
                failures.Add(r);
                if (failures.Count >= 10) break;   // cap the report; 10 failures is enough signal
            }
        }

        // Anti-fake guard: the file must actually EXECUTE a substantial body of MOVE cases (not be entirely
        // deferred-as-exception, which would make the gate vacuous).
        Assert.True(executed > 0, $"{file}: 0 executed (non-exception) cases — the gate would be vacuous");

        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ failures over {executed} executed cases ({deferred} deferred to M4.5d) " +
            $"({(timingAxis ? "data+timing" : "data")} axis):\n{string.Join("\n", failures)}");
    }
}

// One derived class per file — each is its own collection → each runs on its own thread.
public sealed class M68000Move_MOVE_b      : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVE.b.json.gz"; }
public sealed class M68000Move_MOVE_w      : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVE.w.json.gz"; }
public sealed class M68000Move_MOVE_l      : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVE.l.json.gz"; }
public sealed class M68000Move_MOVEA_w     : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVEA.w.json.gz"; }
public sealed class M68000Move_MOVEA_l     : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVEA.l.json.gz"; }
public sealed class M68000Move_MOVEfromSR  : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVEfromSR.json.gz"; }
public sealed class M68000Move_MOVEtoSR    : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVEtoSR.json.gz"; }
public sealed class M68000Move_MOVEtoCCR   : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVEtoCCR.json.gz"; }
public sealed class M68000Move_MOVEfromUSP : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVEfromUSP.json.gz"; }
public sealed class M68000Move_MOVEtoUSP   : M68000MoveTomHarteSweepBase { protected override string VectorFile => "MOVEtoUSP.json.gz"; }

/// <summary>Structural guard: the per-file derived classes must cover EXACTLY the canonical file list — no file
/// dropped, none duplicated. This is what protects the test total against a copy-paste slip in the split.</summary>
public sealed class M68000MoveTomHarteCoverageGuard
{
    [Fact]
    public void Split_classes_cover_exactly_the_canonical_move_file_list()
    {
        var expected = M68000MoveTomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
        var covered = typeof(M68000MoveTomHarteSweepBase).Assembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && typeof(M68000MoveTomHarteSweepBase).IsAssignableFrom(t))
            .Select(t => ((M68000MoveTomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
            .OrderBy(x => x).ToArray();
        Assert.Equal(expected, covered);
    }
}
