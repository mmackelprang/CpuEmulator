using System.Linq;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>The single source of truth for the 680x0 DATA-AXIS vector-file corpus shared by the M4.6 JIT
/// tier-parity sweep (and the Task-3 smoke fact). <see cref="Files"/> is the DEDUPLICATED union of the four
/// canonical per-milestone file lists (MOVE + ALU + M4.5c shift/rotate/bit/BCD/Scc/data-movement + M4.5d-1
/// control-flow/exceptions). It references the existing public <c>CanonicalFiles</c> arrays directly so the JIT
/// corpus can NEVER drift from the interpreter corpus (no hand-retyped file names). DIVU/DIVS appear in both
/// the ALU and M4.5d-1 lists; <see cref="Enumerable.Distinct{TSource}"/> includes each once.
///
/// <para><see cref="IsExcludedCase"/> carries the interpreter sweeps' corpus-artifact exclusions forward into the
/// JIT sweep: because RunCaseThroughJit runs the SAME interpreter via the all-fallback valve, an excluded case
/// would produce the SAME "failure" the interpreter sweeps avoid by skipping. The two predicate bodies are copied
/// VERBATIM from the private filters in M68000M45cTomHarteTests (the inconsistent-register-shift ASL.b artifact)
/// and M68000M45d1TomHarteTests (the CHK in-range UNPREDICTABLE-CCR artifact) so the JIT corpus is identical in
/// EXECUTED cases to the interpreter corpus.</para></summary>
internal static class M68000DataAxisCorpus
{
    /// <summary>The deduplicated, Ordinal-sorted union of the four canonical milestone file lists. Built at
    /// static-init from the existing public arrays (no re-typed names → no drift).</summary>
    public static readonly string[] Files =
        M68000MoveTomHarteSweepBase.CanonicalFiles
        .Concat(M68000AluTomHarteSweepBase.CanonicalFiles)
        .Concat(M68000M45cTomHarteSweepBase.CanonicalFiles)
        .Concat(M68000M45d1TomHarteSweepBase.CanonicalFiles)
        .Distinct()
        .OrderBy(f => f, System.StringComparer.Ordinal)
        .ToArray();

    /// <summary>True when the case is a corpus artifact the interpreter data-axis sweeps EXCLUDE — so the JIT
    /// sweep (which runs the same interpreter via the fallback) must exclude it identically, keeping the two
    /// corpora identical in executed cases. The union of the two milestone filters (copied verbatim below).</summary>
    public static bool IsExcludedCase(M68000TomHarteCase c) =>
        IsInconsistentRegisterShiftVector(c) || IsChkInRangeCase(c);

    /// <summary>Copied VERBATIM from M68000M45cTomHarteSweepBase.IsInconsistentRegisterShiftVector. A handful of
    /// SingleStepTests/680x0 cases are internally INCONSISTENT: for a register-form shift with a Dn target, the
    /// expected FINAL Dn changes bits ABOVE the operand size (.b/.w), which no real shift can produce (a .b/.w
    /// shift writes only the low byte/word; the upper bits are physically preserved). Such a final state is
    /// unreachable, so the vector is a corpus artifact — NOT an emulator bug. Excludes only these provably-
    /// impossible cases (currently exactly 2, both in ASL.b).</summary>
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

    /// <summary>Copied VERBATIM from M68000M45d1TomHarteSweepBase.IsChkInRangeCase. A CHK case (operword
    /// 0xF1C0/0x4180) that does NOT take an exception — i.e. Dn is in [0, bound], the no-trap path, where the
    /// 68000 leaves N/Z/V/C UNPREDICTABLE (PRM; vector-confirmed not a clean function of the operands). Excluded
    /// from the data-axis CCR assertion. The CHK TRAP cases (IsExceptionCase true) are NOT excluded — their CCR
    /// is deterministic and IS asserted.</summary>
    private static bool IsChkInRangeCase(M68000TomHarteCase c)
    {
        if (c.Initial.Prefetch.Length == 0) return false;
        uint ow = c.Initial.Prefetch[0];
        if ((ow & 0xF1C0u) != 0x4180u) return false;          // not a CHK operword
        return !M68000TomHarteRunner.IsExceptionCase(c);      // in-range = no trap taken
    }
}
