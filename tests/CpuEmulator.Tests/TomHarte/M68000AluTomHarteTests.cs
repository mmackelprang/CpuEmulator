using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M4.5b: the integer-ALU-family TomHarte green sweep — the un-fakeable, silicon-derived ground-truth gate.
/// Runs EVERY non-exception case of the 51 in-scope ALU-family files through the real Step+diff runner and
/// asserts the DATA axis (final D0–D7, A0–A6, USP, SSP, SR, RAM) byte-exact (ADR 0007 §6). The operword is
/// seeded from initial.prefetch[0] (the runner already does this; UNCHANGED). The TIMING axis
/// (final.pc/prefetch/per-transaction trace/cycle) is M4.5d; the DIVU/DIVS divide-by-zero exception (vector 5)
/// + any address-error/privilege case is DEFERRED (M4.5d) via the runner's IsExceptionCase heuristic — counted
/// as deferred, NOT asserted.
///
/// HONESTY: the immediate forms (ADDI/SUBI/ANDI/ORI/EORI/CMPI) and quick forms (ADDQ/SUBQ) are NOT in this
/// sweep — NO v1 vector files exist for them (ADR 0007 D1). They EXECUTE and are covered by differential-
/// equivalence (each ≡ its vector-proven reg↔EA counterpart) + synthetic fetch tests in M68000AluExecuteTests,
/// an explicitly-disclosed gap. CMPM is dropped (absent from the dataset). All shift/rotate/bit/BCD/system files
/// are M4.5c (not here).
///
/// Skip-when-absent (vector-less environments) but MUST run green with the vectors PRESENT for merge — a skip is
/// not a mergeable state (ADR 0007 §6 gate 2).
/// </summary>
public class M68000AluTomHarteTests
{
    // The 51 in-scope integer-ALU files (confirmed against the live 68000/v1 tree). Each is mnemonic+size-keyed,
    // gzipped, ~8065 cases.
    public static IEnumerable<object[]> AluFiles =>
    [
        ["ADD.b.json.gz"], ["ADD.w.json.gz"], ["ADD.l.json.gz"],
        ["ADDA.w.json.gz"], ["ADDA.l.json.gz"],
        ["ADDX.b.json.gz"], ["ADDX.w.json.gz"], ["ADDX.l.json.gz"],
        ["SUB.b.json.gz"], ["SUB.w.json.gz"], ["SUB.l.json.gz"],
        ["SUBA.w.json.gz"], ["SUBA.l.json.gz"],
        ["SUBX.b.json.gz"], ["SUBX.w.json.gz"], ["SUBX.l.json.gz"],
        ["AND.b.json.gz"], ["AND.w.json.gz"], ["AND.l.json.gz"],
        ["OR.b.json.gz"], ["OR.w.json.gz"], ["OR.l.json.gz"],
        ["EOR.b.json.gz"], ["EOR.w.json.gz"], ["EOR.l.json.gz"],
        ["CMP.b.json.gz"], ["CMP.w.json.gz"], ["CMP.l.json.gz"],
        ["CMPA.w.json.gz"], ["CMPA.l.json.gz"],
        ["NEG.b.json.gz"], ["NEG.w.json.gz"], ["NEG.l.json.gz"],
        ["NEGX.b.json.gz"], ["NEGX.w.json.gz"], ["NEGX.l.json.gz"],
        ["NOT.b.json.gz"], ["NOT.w.json.gz"], ["NOT.l.json.gz"],
        ["CLR.b.json.gz"], ["CLR.w.json.gz"], ["CLR.l.json.gz"],
        ["TST.b.json.gz"], ["TST.w.json.gz"], ["TST.l.json.gz"],
        ["EXT.w.json.gz"], ["EXT.l.json.gz"],
        ["MULU.json.gz"], ["MULS.json.gz"], ["DIVU.json.gz"], ["DIVS.json.gz"],
    ];

    [M68000TomHarteTheory]
    [MemberData(nameof(AluFiles))]
    public void Alu_family_is_TomHarte_green_on_the_data_axis(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);   // the theory is skipped at discovery when vectors are absent; present == not null
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"in-scope ALU-family vector file missing: {path}");

        var cases = M68000TomHarteLoader.LoadFile(path);
        var failures = new List<string>();
        int executed = 0;     // non-exception ALU cases actually run + asserted on the data axis
        int deferred = 0;     // exception cases (DIVU/DIVS ÷0, address-error/privilege) — M4.5d, counted not asserted
        int outOfScope = 0;   // CMPM cases EMBEDDED in the CMP files (M4.5c) — see below
        foreach (var c in cases)
        {
            // The CMP.b/.w/.l vector files BUNDLE the CMPM cases ((Ay)+,(Ax)+ = encoding 1011 yyy 1 ss 001 xxx,
            // mask 0xF138 == 0xB108). CMPM is ABSENT from the FieldGrammar dataset (ADR 0007 D1 — dropped from
            // M4.5b; it is M4.5c, requiring a dataset row). In our decoder that opcode SLOT collides with EOR's
            // mask, so it would mis-decode as EOR and fail. These are NOT plain CMP — skip them as out-of-scope
            // (NOT asserted, NOT a vacuous-green hole: the ~6500+ plain-CMP cases per file ARE asserted). This is
            // the honest M4.5c boundary, analogous to MOVE.q / MOVEM being excluded from the M4.5a MOVE sweep.
            uint operword = c.Initial.Prefetch.Length > 0 ? c.Initial.Prefetch[0] : 0u;
            if ((operword & 0xF138u) == 0xB108u) { outOfScope++; continue; }   // CMPM -> M4.5c

            string? r = M68000TomHarteRunner.RunCase(c);          // data axis (timingAxis: false)
            if (ReferenceEquals(r, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (r is not null)
            {
                failures.Add(r);
                if (failures.Count >= 10) break;   // cap the report; 10 failures is enough signal
            }
        }

        // Anti-fake guard: the file must actually EXECUTE a substantial body of cases (not be entirely deferred-
        // as-exception or out-of-scope, which would make the gate vacuous). Every ALU file has thousands of
        // in-scope non-exception cases.
        Assert.True(executed > 0, $"{file}: 0 executed (non-exception) cases — the gate would be vacuous");

        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ data-axis failures over {executed} executed cases " +
            $"({deferred} deferred to M4.5d, {outOfScope} CMPM out-of-scope to M4.5c):\n" +
            string.Join("\n", failures));
    }
}
