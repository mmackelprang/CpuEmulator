using System.Linq;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>The single source of truth for the 8086/8088 DATA-AXIS vector-file corpus shared by the M5.6 JIT
/// tier-parity sweep. <see cref="Files"/> is the DEDUPLICATED union of the four canonical per-family file lists
/// (MOV + ALU/BCD/F6F7 + shift/rotate/stack/misc + control-flow/strings/interrupts). It references the existing
/// public <c>CanonicalFiles</c> arrays directly so the JIT corpus can NEVER drift from the interpreter corpus
/// (no hand-retyped file names).
///
/// <para>UNLIKE the 68000 (whose data-axis corpus carries an <c>IsExcludedCase</c> predicate for two corpus
/// artifacts — the ASL.b inconsistent-register-shift vectors + the CHK in-range UNPREDICTABLE-CCR cases), the
/// 8086 has NO per-CASE corpus-artifact pre-exclusions. The 8086 deferrals — the divide-error (INT0) undefined-
/// flag fallout (DD6) and the IDIV quotient-sign quirk — are CLASSIFIER-based (run-then-classify): the sweep
/// runs the real instruction, then confirms a FAILING case is precisely the documented quirk before deferring
/// (mirroring the interpreter family sweeps in <see cref="M8088AluTomHarteSweepBase"/>). So this corpus needs
/// ONLY the <see cref="Files"/> union; the deferrals live in the sweep's run-then-classify path, not here.</para></summary>
internal static class M8088DataAxisCorpus
{
    /// <summary>The deduplicated, Ordinal-sorted union of the four canonical family file lists. Built at
    /// static-init from the existing public arrays (no re-typed names → no drift).</summary>
    public static readonly string[] Files =
        M8088MovTomHarteSweepBase.CanonicalFiles
        .Concat(M8088AluTomHarteSweepBase.CanonicalFiles)
        .Concat(M8088ShiftStackMiscTomHarteSweepBase.CanonicalFiles)
        .Concat(M8088ControlStringsIntTomHarteSweepBase.CanonicalFiles)
        .Distinct()
        .OrderBy(f => f, System.StringComparer.Ordinal)
        .ToArray();
}
