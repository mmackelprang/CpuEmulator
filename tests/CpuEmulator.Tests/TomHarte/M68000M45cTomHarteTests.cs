using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.5c: the shift/rotate + bit + BCD + Scc + data-movement TomHarte green sweep (42 dedicated files)
/// — the un-fakeable data-axis gate. EVERY M4.5c core op has a dedicated v1 vector file. The TIMING axis is
/// M4.5d; exception cases defer via IsExceptionCase. NOTE: MOVEQ's vector file is named MOVE.q.json.gz; MOVEP
/// (DC5) is INCLUDED (.w + .l).
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> Each in-scope file gets its OWN derived class
/// (its own xUnit collection); the assertion body — including the <see cref="M68000M45cTomHarteSweepBase"/>'s
/// inconsistent-register-shift-vector filter — is IDENTICAL to the pre-split single-theory body. The coverage
/// guard asserts exact coverage of <see cref="CanonicalFiles"/>.</para>
///
/// <para>Routine/CI runs cap each file at a 200-case sample (CPUEMULATOR_TOMHARTE_SAMPLE); the authoritative
/// substantive/milestone merge gate runs CPUEMULATOR_UAT=full (the full ~8065-case-per-file sweep).</para></summary>
public abstract class M68000M45cTomHarteSweepBase
{
    public static readonly string[] CanonicalFiles =
    [
        // shift/rotate (24)
        "ASL.b.json.gz", "ASL.w.json.gz", "ASL.l.json.gz",
        "ASR.b.json.gz", "ASR.w.json.gz", "ASR.l.json.gz",
        "LSL.b.json.gz", "LSL.w.json.gz", "LSL.l.json.gz",
        "LSR.b.json.gz", "LSR.w.json.gz", "LSR.l.json.gz",
        "ROL.b.json.gz", "ROL.w.json.gz", "ROL.l.json.gz",
        "ROR.b.json.gz", "ROR.w.json.gz", "ROR.l.json.gz",
        "ROXL.b.json.gz", "ROXL.w.json.gz", "ROXL.l.json.gz",
        "ROXR.b.json.gz", "ROXR.w.json.gz", "ROXR.l.json.gz",
        // bit (4)
        "BTST.json.gz", "BCHG.json.gz", "BCLR.json.gz", "BSET.json.gz",
        // BCD (3)
        "ABCD.json.gz", "SBCD.json.gz", "NBCD.json.gz",
        // Scc (1)
        "Scc.json.gz",
        // data-movement (8) — MOVEQ's vector file is MOVE.q.json.gz
        "SWAP.json.gz", "EXG.json.gz", "LEA.json.gz", "PEA.json.gz",
        "MOVE.q.json.gz", "TAS.json.gz", "MOVEM.w.json.gz", "MOVEM.l.json.gz",
        // MOVEP (2) — DC5 INCLUDED
        "MOVEP.w.json.gz", "MOVEP.l.json.gz",
    ];

    protected abstract string VectorFile { get; }
    public string FileForGuard => VectorFile;

    [M68000TomHarteFact]
    public void M45c_family_is_TomHarte_green_on_the_data_axis()
    {
        string file = VectorFile;
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"in-scope M4.5c vector file missing: {path}");

        int sampleSize = M68000TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M68000.Get(path, sampleSize,
            max => M68000TomHarteLoader.LoadFile(path, max));
        var failures = new List<string>();
        int run = 0;
        int executed = 0, deferred = 0, inconsistent = 0;
        foreach (var c in cases)
        {
            if (run >= sampleSize) break;
            run++;
            if (IsInconsistentRegisterShiftVector(c)) { inconsistent++; continue; }   // corpus artifact (see below)
            string? rr = M68000TomHarteRunner.RunCase(c);            // data axis (timingAxis: false)
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 10) break; }
        }
        Assert.True(executed > 0, $"{file}: 0 executed cases — the gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ data-axis failures over {executed} executed ({deferred} deferred, {inconsistent} inconsistent):\n" +
            string.Join("\n", failures));
    }

    /// <summary>A handful of SingleStepTests/680x0 cases are internally INCONSISTENT: for a register-form shift
    /// with a Dn target, the expected FINAL Dn changes bits ABOVE the operand size (.b/.w), which no real shift
    /// can produce (a .b/.w shift writes only the low byte/word; the upper bits are physically preserved). Such a
    /// final state is unreachable, so the vector is a corpus artifact — NOT an emulator bug (the engine produces
    /// the semantically-correct partial-write result). We exclude only these provably-impossible cases (currently
    /// exactly 2, both in ASL.b). This is a narrow, content-derived exclusion, not a "skip what we can't pass."</summary>
    private static bool IsInconsistentRegisterShiftVector(M68000TomHarteCase c)
    {
        if (c.Initial.Prefetch.Length == 0) return false;
        uint ow = c.Initial.Prefetch[0];
        if ((ow & 0xF000u) != 0xE000u) return false;       // not a shift/rotate
        if ((ow & 0x00C0u) == 0x00C0u) return false;       // the memory-by-1 form (size bits = 11) has no Dn target
        uint sizeBits = (ow >> 6) & 3u;                    // 0=.b, 1=.w, 2=.l
        if (sizeBits == 2u) return false;                  // .l touches the whole register — nothing preserved
        uint dn = ow & 7u;
        uint upperMask = sizeBits == 0u ? 0xFFFFFF00u : 0xFFFF0000u;   // bits the op MUST preserve
        return (c.Initial.D[dn] & upperMask) != (c.Final.D[dn] & upperMask);
    }
}

public sealed class M68000M45c_ASL_b   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ASL.b.json.gz"; }
public sealed class M68000M45c_ASL_w   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ASL.w.json.gz"; }
public sealed class M68000M45c_ASL_l   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ASL.l.json.gz"; }
public sealed class M68000M45c_ASR_b   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ASR.b.json.gz"; }
public sealed class M68000M45c_ASR_w   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ASR.w.json.gz"; }
public sealed class M68000M45c_ASR_l   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ASR.l.json.gz"; }
public sealed class M68000M45c_LSL_b   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "LSL.b.json.gz"; }
public sealed class M68000M45c_LSL_w   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "LSL.w.json.gz"; }
public sealed class M68000M45c_LSL_l   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "LSL.l.json.gz"; }
public sealed class M68000M45c_LSR_b   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "LSR.b.json.gz"; }
public sealed class M68000M45c_LSR_w   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "LSR.w.json.gz"; }
public sealed class M68000M45c_LSR_l   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "LSR.l.json.gz"; }
public sealed class M68000M45c_ROL_b   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROL.b.json.gz"; }
public sealed class M68000M45c_ROL_w   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROL.w.json.gz"; }
public sealed class M68000M45c_ROL_l   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROL.l.json.gz"; }
public sealed class M68000M45c_ROR_b   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROR.b.json.gz"; }
public sealed class M68000M45c_ROR_w   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROR.w.json.gz"; }
public sealed class M68000M45c_ROR_l   : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROR.l.json.gz"; }
public sealed class M68000M45c_ROXL_b  : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROXL.b.json.gz"; }
public sealed class M68000M45c_ROXL_w  : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROXL.w.json.gz"; }
public sealed class M68000M45c_ROXL_l  : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROXL.l.json.gz"; }
public sealed class M68000M45c_ROXR_b  : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROXR.b.json.gz"; }
public sealed class M68000M45c_ROXR_w  : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROXR.w.json.gz"; }
public sealed class M68000M45c_ROXR_l  : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ROXR.l.json.gz"; }
public sealed class M68000M45c_BTST    : M68000M45cTomHarteSweepBase { protected override string VectorFile => "BTST.json.gz"; }
public sealed class M68000M45c_BCHG    : M68000M45cTomHarteSweepBase { protected override string VectorFile => "BCHG.json.gz"; }
public sealed class M68000M45c_BCLR    : M68000M45cTomHarteSweepBase { protected override string VectorFile => "BCLR.json.gz"; }
public sealed class M68000M45c_BSET    : M68000M45cTomHarteSweepBase { protected override string VectorFile => "BSET.json.gz"; }
public sealed class M68000M45c_ABCD    : M68000M45cTomHarteSweepBase { protected override string VectorFile => "ABCD.json.gz"; }
public sealed class M68000M45c_SBCD    : M68000M45cTomHarteSweepBase { protected override string VectorFile => "SBCD.json.gz"; }
public sealed class M68000M45c_NBCD    : M68000M45cTomHarteSweepBase { protected override string VectorFile => "NBCD.json.gz"; }
public sealed class M68000M45c_Scc     : M68000M45cTomHarteSweepBase { protected override string VectorFile => "Scc.json.gz"; }
public sealed class M68000M45c_SWAP    : M68000M45cTomHarteSweepBase { protected override string VectorFile => "SWAP.json.gz"; }
public sealed class M68000M45c_EXG     : M68000M45cTomHarteSweepBase { protected override string VectorFile => "EXG.json.gz"; }
public sealed class M68000M45c_LEA     : M68000M45cTomHarteSweepBase { protected override string VectorFile => "LEA.json.gz"; }
public sealed class M68000M45c_PEA     : M68000M45cTomHarteSweepBase { protected override string VectorFile => "PEA.json.gz"; }
public sealed class M68000M45c_MOVE_q  : M68000M45cTomHarteSweepBase { protected override string VectorFile => "MOVE.q.json.gz"; }
public sealed class M68000M45c_TAS     : M68000M45cTomHarteSweepBase { protected override string VectorFile => "TAS.json.gz"; }
public sealed class M68000M45c_MOVEM_w : M68000M45cTomHarteSweepBase { protected override string VectorFile => "MOVEM.w.json.gz"; }
public sealed class M68000M45c_MOVEM_l : M68000M45cTomHarteSweepBase { protected override string VectorFile => "MOVEM.l.json.gz"; }
public sealed class M68000M45c_MOVEP_w : M68000M45cTomHarteSweepBase { protected override string VectorFile => "MOVEP.w.json.gz"; }
public sealed class M68000M45c_MOVEP_l : M68000M45cTomHarteSweepBase { protected override string VectorFile => "MOVEP.l.json.gz"; }

/// <summary>Structural guard: the per-file derived classes cover EXACTLY the canonical M4.5c file list.</summary>
public sealed class M68000M45cTomHarteCoverageGuard
{
    [Fact]
    public void Split_classes_cover_exactly_the_canonical_m45c_file_list()
    {
        var expected = M68000M45cTomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
        var covered = typeof(M68000M45cTomHarteSweepBase).Assembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && typeof(M68000M45cTomHarteSweepBase).IsAssignableFrom(t))
            .Select(t => ((M68000M45cTomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
            .OrderBy(x => x).ToArray();
        Assert.Equal(expected, covered);
    }
}
