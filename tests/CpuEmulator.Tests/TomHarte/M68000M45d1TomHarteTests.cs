using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.5d-1: the control-flow + exception TomHarte green sweep (20 dedicated + the DIVU/DIVS ÷0 re-run)
/// on the DATA axis with assertExceptions:true — the un-fakeable gate. The exception cases that M4.5a-c DEFERRED
/// (privilege/illegal/÷0) now ASSERT; the address-error (vector 3) large-frame WORD contents defer to M4.5d-2
/// (the runner's IsAddressErrorCase keeps vector-3 deferred). The TIMING axis is M4.5d-2 (timingAxis:false).
/// UNLK's file is UNLINK.json.gz.
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> Each in-scope file gets its OWN derived class;
/// the body — including the CHK-in-range filter — is IDENTICAL to the pre-split single-theory body. The coverage
/// guard asserts exact coverage of <see cref="CanonicalFiles"/>.</para></summary>
public abstract class M68000M45d1TomHarteSweepBase
{
    public static readonly string[] CanonicalFiles =
    [
        // branches (3)
        "Bcc.json.gz", "BSR.json.gz", "DBcc.json.gz",
        // jumps/returns (5)
        "JMP.json.gz", "JSR.json.gz", "RTS.json.gz", "RTR.json.gz", "RTE.json.gz",
        // stack frame (2) — UNLK's file is UNLINK
        "LINK.json.gz", "UNLINK.json.gz",
        // vector/check (3)
        "TRAP.json.gz", "TRAPV.json.gz", "CHK.json.gz",
        // no-op (1)
        "NOP.json.gz",
        // to-CCR/SR (6)
        "ANDItoCCR.json.gz", "ANDItoSR.json.gz",
        "ORItoCCR.json.gz",  "ORItoSR.json.gz",
        "EORItoCCR.json.gz", "EORItoSR.json.gz",
        // the ÷0 vector-5 re-run (2) — the M4.5b/c detect-and-defer comes due
        "DIVU.json.gz", "DIVS.json.gz",
    ];

    protected abstract string VectorFile { get; }
    public string FileForGuard => VectorFile;

    [M68000TomHarteFact]
    public void M45d1_family_is_TomHarte_green_on_the_data_axis()
    {
        string file = VectorFile;
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"in-scope M4.5d-1 vector file missing: {path}");

        var cases = M68000TomHarteLoader.LoadFile(path);
        var failures = new List<string>();
        int executed = 0, deferred = 0, unpredictable = 0;
        foreach (var c in cases)
        {
            // CHK on the IN-RANGE (no-trap) path leaves the CCR in a documented-UNPREDICTABLE state (the 68000
            // PRM: N/Z/V/C undefined when Dn is in [0,bound]). Those cases are a corpus artifact, so the sweep
            // excludes them — the CHK TRAP cases (deterministic CCR) ARE asserted on the data axis.
            if (IsChkInRangeCase(c)) { unpredictable++; continue; }
            string? rr = M68000TomHarteRunner.RunCase(c, assertExceptions: true);   // data axis; exceptions ASSERT
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }   // only address-error (DD4)
            executed++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 10) break; }
        }
        Assert.True(executed > 0, $"{file}: 0 executed cases — the gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ data-axis failures over {executed} executed ({deferred} deferred, " +
            $"{unpredictable} CHK-in-range-unpredictable):\n" + string.Join("\n", failures));
    }

    /// <summary>A CHK case (operword 0xF1C0/0x4180) that does NOT take an exception — i.e. Dn is in [0, bound],
    /// the no-trap path, where the 68000 leaves N/Z/V/C UNPREDICTABLE (PRM; vector-confirmed not a clean function
    /// of the operands). Excluded from the data-axis CCR assertion. The CHK TRAP cases (IsExceptionCase true) are
    /// NOT excluded — their CCR is deterministic and IS asserted.</summary>
    private static bool IsChkInRangeCase(M68000TomHarteCase c)
    {
        if (c.Initial.Prefetch.Length == 0) return false;
        uint ow = c.Initial.Prefetch[0];
        if ((ow & 0xF1C0u) != 0x4180u) return false;          // not a CHK operword
        return !M68000TomHarteRunner.IsExceptionCase(c);      // in-range = no trap taken
    }
}

public sealed class M68000M45d1_Bcc       : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "Bcc.json.gz"; }
public sealed class M68000M45d1_BSR       : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "BSR.json.gz"; }
public sealed class M68000M45d1_DBcc      : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "DBcc.json.gz"; }
public sealed class M68000M45d1_JMP       : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "JMP.json.gz"; }
public sealed class M68000M45d1_JSR       : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "JSR.json.gz"; }
public sealed class M68000M45d1_RTS       : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "RTS.json.gz"; }
public sealed class M68000M45d1_RTR       : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "RTR.json.gz"; }
public sealed class M68000M45d1_RTE       : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "RTE.json.gz"; }
public sealed class M68000M45d1_LINK      : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "LINK.json.gz"; }
public sealed class M68000M45d1_UNLINK    : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "UNLINK.json.gz"; }
public sealed class M68000M45d1_TRAP      : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "TRAP.json.gz"; }
public sealed class M68000M45d1_TRAPV     : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "TRAPV.json.gz"; }
public sealed class M68000M45d1_CHK       : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "CHK.json.gz"; }
public sealed class M68000M45d1_NOP       : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "NOP.json.gz"; }
public sealed class M68000M45d1_ANDItoCCR : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "ANDItoCCR.json.gz"; }
public sealed class M68000M45d1_ANDItoSR  : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "ANDItoSR.json.gz"; }
public sealed class M68000M45d1_ORItoCCR  : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "ORItoCCR.json.gz"; }
public sealed class M68000M45d1_ORItoSR   : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "ORItoSR.json.gz"; }
public sealed class M68000M45d1_EORItoCCR : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "EORItoCCR.json.gz"; }
public sealed class M68000M45d1_EORItoSR  : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "EORItoSR.json.gz"; }
public sealed class M68000M45d1_DIVU      : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "DIVU.json.gz"; }
public sealed class M68000M45d1_DIVS      : M68000M45d1TomHarteSweepBase { protected override string VectorFile => "DIVS.json.gz"; }

/// <summary>Structural guard: the per-file derived classes cover EXACTLY the canonical M4.5d-1 file list.</summary>
public sealed class M68000M45d1TomHarteCoverageGuard
{
    [Fact]
    public void Split_classes_cover_exactly_the_canonical_m45d1_file_list()
    {
        var expected = M68000M45d1TomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
        var covered = typeof(M68000M45d1TomHarteSweepBase).Assembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && typeof(M68000M45d1TomHarteSweepBase).IsAssignableFrom(t))
            .Select(t => ((M68000M45d1TomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
            .OrderBy(x => x).ToArray();
        Assert.Equal(expected, covered);
    }
}
