using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M4.5b: the integer-ALU-family TomHarte green sweep — the un-fakeable, silicon-derived ground-truth gate.
/// Runs EVERY non-exception case of the 51 in-scope ALU-family files through the real Step+diff runner and
/// asserts the DATA axis (final D0–D7, A0–A6, USP, SSP, SR, RAM) byte-exact (ADR 0007 §6).
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> Each in-scope file gets its OWN derived class
/// (its own xUnit collection) so the 51 files distribute across cores instead of running serially in one theory
/// class. The assertion body is IDENTICAL to the pre-split single-theory body — only the collection boundary
/// changed. <see cref="CanonicalFiles"/> is the source of truth; the coverage guard asserts exact coverage.</para>
///
/// HONESTY: the immediate forms (ADDI/SUBI/ANDI/ORI/EORI/CMPI) and quick forms (ADDQ/SUBQ) are NOT in this
/// sweep — NO v1 vector files exist for them (ADR 0007 D1). CMPM IS asserted (its cases are bundled in
/// CMP.b/.w/.l + CMPA.l and flow through RunCase like any other case). Skip-when-absent but MUST run green with
/// the vectors PRESENT for merge — a skip is not a mergeable state (ADR 0007 §6 gate 2).
/// </summary>
public abstract class M68000AluTomHarteSweepBase
{
    /// <summary>The 51 in-scope integer-ALU files (mnemonic+size-keyed, gzipped, ~8065 cases each). The source
    /// of truth for the coverage guard.</summary>
    public static readonly string[] CanonicalFiles =
    [
        "ADD.b.json.gz", "ADD.w.json.gz", "ADD.l.json.gz",
        "ADDA.w.json.gz", "ADDA.l.json.gz",
        "ADDX.b.json.gz", "ADDX.w.json.gz", "ADDX.l.json.gz",
        "SUB.b.json.gz", "SUB.w.json.gz", "SUB.l.json.gz",
        "SUBA.w.json.gz", "SUBA.l.json.gz",
        "SUBX.b.json.gz", "SUBX.w.json.gz", "SUBX.l.json.gz",
        "AND.b.json.gz", "AND.w.json.gz", "AND.l.json.gz",
        "OR.b.json.gz", "OR.w.json.gz", "OR.l.json.gz",
        "EOR.b.json.gz", "EOR.w.json.gz", "EOR.l.json.gz",
        "CMP.b.json.gz", "CMP.w.json.gz", "CMP.l.json.gz",
        "CMPA.w.json.gz", "CMPA.l.json.gz",
        "NEG.b.json.gz", "NEG.w.json.gz", "NEG.l.json.gz",
        "NEGX.b.json.gz", "NEGX.w.json.gz", "NEGX.l.json.gz",
        "NOT.b.json.gz", "NOT.w.json.gz", "NOT.l.json.gz",
        "CLR.b.json.gz", "CLR.w.json.gz", "CLR.l.json.gz",
        "TST.b.json.gz", "TST.w.json.gz", "TST.l.json.gz",
        "EXT.w.json.gz", "EXT.l.json.gz",
        "MULU.json.gz", "MULS.json.gz", "DIVU.json.gz", "DIVS.json.gz",
    ];

    protected abstract string VectorFile { get; }
    public string FileForGuard => VectorFile;

    [M68000TomHarteFact]
    public void Alu_family_is_TomHarte_green_on_the_data_axis()
    {
        string file = VectorFile;
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);   // the test is skipped at discovery when vectors are absent; present == not null
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"in-scope ALU-family vector file missing: {path}");

        var cases = M68000TomHarteLoader.LoadFile(path);
        var failures = new List<string>();
        int executed = 0;     // non-exception ALU cases actually run + asserted on the data axis
        int deferred = 0;     // exception cases (DIVU/DIVS ÷0, address-error/privilege) — M4.5d, counted not asserted
        foreach (var c in cases)
        {
            string? r = M68000TomHarteRunner.RunCase(c);          // data axis (timingAxis: false)
            if (ReferenceEquals(r, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (r is not null)
            {
                failures.Add(r);
                if (failures.Count >= 10) break;   // cap the report; 10 failures is enough signal
            }
        }

        Assert.True(executed > 0, $"{file}: 0 executed (non-exception) cases — the gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ data-axis failures over {executed} executed cases " +
            $"({deferred} deferred to M4.5d):\n" +
            string.Join("\n", failures));
    }
}

public sealed class M68000Alu_ADD_b  : M68000AluTomHarteSweepBase { protected override string VectorFile => "ADD.b.json.gz"; }
public sealed class M68000Alu_ADD_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "ADD.w.json.gz"; }
public sealed class M68000Alu_ADD_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "ADD.l.json.gz"; }
public sealed class M68000Alu_ADDA_w : M68000AluTomHarteSweepBase { protected override string VectorFile => "ADDA.w.json.gz"; }
public sealed class M68000Alu_ADDA_l : M68000AluTomHarteSweepBase { protected override string VectorFile => "ADDA.l.json.gz"; }
public sealed class M68000Alu_ADDX_b : M68000AluTomHarteSweepBase { protected override string VectorFile => "ADDX.b.json.gz"; }
public sealed class M68000Alu_ADDX_w : M68000AluTomHarteSweepBase { protected override string VectorFile => "ADDX.w.json.gz"; }
public sealed class M68000Alu_ADDX_l : M68000AluTomHarteSweepBase { protected override string VectorFile => "ADDX.l.json.gz"; }
public sealed class M68000Alu_SUB_b  : M68000AluTomHarteSweepBase { protected override string VectorFile => "SUB.b.json.gz"; }
public sealed class M68000Alu_SUB_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "SUB.w.json.gz"; }
public sealed class M68000Alu_SUB_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "SUB.l.json.gz"; }
public sealed class M68000Alu_SUBA_w : M68000AluTomHarteSweepBase { protected override string VectorFile => "SUBA.w.json.gz"; }
public sealed class M68000Alu_SUBA_l : M68000AluTomHarteSweepBase { protected override string VectorFile => "SUBA.l.json.gz"; }
public sealed class M68000Alu_SUBX_b : M68000AluTomHarteSweepBase { protected override string VectorFile => "SUBX.b.json.gz"; }
public sealed class M68000Alu_SUBX_w : M68000AluTomHarteSweepBase { protected override string VectorFile => "SUBX.w.json.gz"; }
public sealed class M68000Alu_SUBX_l : M68000AluTomHarteSweepBase { protected override string VectorFile => "SUBX.l.json.gz"; }
public sealed class M68000Alu_AND_b  : M68000AluTomHarteSweepBase { protected override string VectorFile => "AND.b.json.gz"; }
public sealed class M68000Alu_AND_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "AND.w.json.gz"; }
public sealed class M68000Alu_AND_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "AND.l.json.gz"; }
public sealed class M68000Alu_OR_b   : M68000AluTomHarteSweepBase { protected override string VectorFile => "OR.b.json.gz"; }
public sealed class M68000Alu_OR_w   : M68000AluTomHarteSweepBase { protected override string VectorFile => "OR.w.json.gz"; }
public sealed class M68000Alu_OR_l   : M68000AluTomHarteSweepBase { protected override string VectorFile => "OR.l.json.gz"; }
public sealed class M68000Alu_EOR_b  : M68000AluTomHarteSweepBase { protected override string VectorFile => "EOR.b.json.gz"; }
public sealed class M68000Alu_EOR_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "EOR.w.json.gz"; }
public sealed class M68000Alu_EOR_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "EOR.l.json.gz"; }
public sealed class M68000Alu_CMP_b  : M68000AluTomHarteSweepBase { protected override string VectorFile => "CMP.b.json.gz"; }
public sealed class M68000Alu_CMP_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "CMP.w.json.gz"; }
public sealed class M68000Alu_CMP_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "CMP.l.json.gz"; }
public sealed class M68000Alu_CMPA_w : M68000AluTomHarteSweepBase { protected override string VectorFile => "CMPA.w.json.gz"; }
public sealed class M68000Alu_CMPA_l : M68000AluTomHarteSweepBase { protected override string VectorFile => "CMPA.l.json.gz"; }
public sealed class M68000Alu_NEG_b  : M68000AluTomHarteSweepBase { protected override string VectorFile => "NEG.b.json.gz"; }
public sealed class M68000Alu_NEG_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "NEG.w.json.gz"; }
public sealed class M68000Alu_NEG_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "NEG.l.json.gz"; }
public sealed class M68000Alu_NEGX_b : M68000AluTomHarteSweepBase { protected override string VectorFile => "NEGX.b.json.gz"; }
public sealed class M68000Alu_NEGX_w : M68000AluTomHarteSweepBase { protected override string VectorFile => "NEGX.w.json.gz"; }
public sealed class M68000Alu_NEGX_l : M68000AluTomHarteSweepBase { protected override string VectorFile => "NEGX.l.json.gz"; }
public sealed class M68000Alu_NOT_b  : M68000AluTomHarteSweepBase { protected override string VectorFile => "NOT.b.json.gz"; }
public sealed class M68000Alu_NOT_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "NOT.w.json.gz"; }
public sealed class M68000Alu_NOT_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "NOT.l.json.gz"; }
public sealed class M68000Alu_CLR_b  : M68000AluTomHarteSweepBase { protected override string VectorFile => "CLR.b.json.gz"; }
public sealed class M68000Alu_CLR_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "CLR.w.json.gz"; }
public sealed class M68000Alu_CLR_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "CLR.l.json.gz"; }
public sealed class M68000Alu_TST_b  : M68000AluTomHarteSweepBase { protected override string VectorFile => "TST.b.json.gz"; }
public sealed class M68000Alu_TST_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "TST.w.json.gz"; }
public sealed class M68000Alu_TST_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "TST.l.json.gz"; }
public sealed class M68000Alu_EXT_w  : M68000AluTomHarteSweepBase { protected override string VectorFile => "EXT.w.json.gz"; }
public sealed class M68000Alu_EXT_l  : M68000AluTomHarteSweepBase { protected override string VectorFile => "EXT.l.json.gz"; }
public sealed class M68000Alu_MULU   : M68000AluTomHarteSweepBase { protected override string VectorFile => "MULU.json.gz"; }
public sealed class M68000Alu_MULS   : M68000AluTomHarteSweepBase { protected override string VectorFile => "MULS.json.gz"; }
public sealed class M68000Alu_DIVU   : M68000AluTomHarteSweepBase { protected override string VectorFile => "DIVU.json.gz"; }
public sealed class M68000Alu_DIVS   : M68000AluTomHarteSweepBase { protected override string VectorFile => "DIVS.json.gz"; }

/// <summary>Structural guard: the per-file derived classes cover EXACTLY the canonical ALU file list.</summary>
public sealed class M68000AluTomHarteCoverageGuard
{
    [Fact]
    public void Split_classes_cover_exactly_the_canonical_alu_file_list()
    {
        var expected = M68000AluTomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
        var covered = typeof(M68000AluTomHarteSweepBase).Assembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && typeof(M68000AluTomHarteSweepBase).IsAssignableFrom(t))
            .Select(t => ((M68000AluTomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
            .OrderBy(x => x).ToArray();
        Assert.Equal(expected, covered);
    }
}
