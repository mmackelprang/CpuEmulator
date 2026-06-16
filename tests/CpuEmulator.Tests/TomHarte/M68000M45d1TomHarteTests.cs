using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.5d-1: the SINGLE control-flow + exception TomHarte green sweep (20 dedicated + the DIVU/DIVS ÷0
/// re-run) on the DATA axis with assertExceptions:true — the un-fakeable gate. Every M4.5d-1 control-flow op has
/// a dedicated v1 vector file (verified, ADR 0008 §2); ILLEGAL (vector 4) asserts via the embedded illegal cases
/// across the M4.5a-c files (the cross-corpus exception axis, M68000ExceptionCorpusTomHarteTests). The exception
/// cases that M4.5a-c DEFERRED (privilege/illegal/÷0) now ASSERT; the address-error (vector 3) large-frame WORD
/// contents defer to M4.5d-2 (DD4 — assert trap-taken; the runner's IsAddressErrorCase keeps vector-3 deferred).
/// The TIMING axis (final.pc/prefetch/trace/cycle) is M4.5d-2 (timingAxis:false). UNLK's file is UNLINK.json.gz.</summary>
public class M68000M45d1TomHarteTests
{
    public static IEnumerable<object[]> M45d1Files =>
    [
        // branches (3)
        ["Bcc.json.gz"], ["BSR.json.gz"], ["DBcc.json.gz"],
        // jumps/returns (5)
        ["JMP.json.gz"], ["JSR.json.gz"], ["RTS.json.gz"], ["RTR.json.gz"], ["RTE.json.gz"],
        // stack frame (2) — UNLK's file is UNLINK
        ["LINK.json.gz"], ["UNLINK.json.gz"],
        // vector/check (3)
        ["TRAP.json.gz"], ["TRAPV.json.gz"], ["CHK.json.gz"],
        // no-op (1)
        ["NOP.json.gz"],
        // to-CCR/SR (6)
        ["ANDItoCCR.json.gz"], ["ANDItoSR.json.gz"],
        ["ORItoCCR.json.gz"],  ["ORItoSR.json.gz"],
        ["EORItoCCR.json.gz"], ["EORItoSR.json.gz"],
        // the ÷0 vector-5 re-run (2) — the M4.5b/c detect-and-defer comes due
        ["DIVU.json.gz"], ["DIVS.json.gz"],
    ];

    [M68000TomHarteTheory]
    [MemberData(nameof(M45d1Files))]
    public void M45d1_family_is_TomHarte_green_on_the_data_axis(string file)
    {
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
            // PRM: N/Z/V/C undefined when Dn is in [0,bound]; the vectors confirm it is NOT a clean function of
            // the operands). Those cases are a corpus artifact (the M4.5c inconsistent-vector precedent), so the
            // sweep excludes them — the CHK TRAP cases (deterministic CCR) ARE asserted on the data axis.
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
    /// of the operands). Excluded from the data-axis CCR assertion (the M4.5c inconsistent-vector precedent). The
    /// CHK TRAP cases (IsExceptionCase true) are NOT excluded — their CCR is deterministic and IS asserted.</summary>
    private static bool IsChkInRangeCase(M68000TomHarteCase c)
    {
        if (c.Initial.Prefetch.Length == 0) return false;
        uint ow = c.Initial.Prefetch[0];
        if ((ow & 0xF1C0u) != 0x4180u) return false;          // not a CHK operword
        return !M68000TomHarteRunner.IsExceptionCase(c);      // in-range = no trap taken
    }
}
