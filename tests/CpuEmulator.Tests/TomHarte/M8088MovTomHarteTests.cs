using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M5.5a: the 8086/8088 MOV-family TomHarte green sweep — the un-fakeable, silicon-derived ground-truth gate for
/// the MOV read/modify/write EA execute pipeline. Runs every case (up to the per-file sample cap) of each MOV
/// opcode file through the real <see cref="M8088TomHarteRunner.RunCase"/> Step + diff and asserts the DATA axis
/// (the 14 registers + the changed RAM cells; FLAGS mask-aware, moot for MOV) byte-exact.
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> Each MOV opcode file gets its OWN derived class
/// (its own xUnit collection) so the files distribute across cores — the M68000AluTomHarteTests pattern.
/// <see cref="CanonicalFiles"/> is the source of truth; the coverage guard asserts exact coverage.</para>
///
/// <para>SCOPE: the MOV family only (88-8E, A0-A3, B0-BF, C6, C7). 8D (LEA), C4/C5 (LES/LDS), 8F (POP r/m) share
/// neither opcode nor body and are NOT MOV — they are later milestones. Skip-when-absent but MUST run green with
/// the vectors PRESENT for merge. Routine/CI runs cap each file at the CPUEMULATOR_TOMHARTE_SAMPLE sample; the
/// authoritative milestone gate runs CPUEMULATOR_UAT=full (the full 10,000-case-per-file sweep).</para>
/// </summary>
public abstract class M8088MovTomHarteSweepBase
{
    /// <summary>The MOV-family opcode files (hex-keyed gzip). The source of truth for the coverage guard:
    /// 88-8E (the d/w + segment-register forms; 8D=LEA is excluded — not a MOV), A0-A3 (accumulator-direct),
    /// B0-BF (the 16 imm→reg forms), and C6/C7 (the imm→r/m group, reg=0).</summary>
    public static readonly string[] CanonicalFiles =
    [
        "88.json.gz", "89.json.gz", "8A.json.gz", "8B.json.gz", "8C.json.gz", "8E.json.gz",
        "A0.json.gz", "A1.json.gz", "A2.json.gz", "A3.json.gz",
        "B0.json.gz", "B1.json.gz", "B2.json.gz", "B3.json.gz",
        "B4.json.gz", "B5.json.gz", "B6.json.gz", "B7.json.gz",
        "B8.json.gz", "B9.json.gz", "BA.json.gz", "BB.json.gz",
        "BC.json.gz", "BD.json.gz", "BE.json.gz", "BF.json.gz",
        "C6.json.gz", "C7.json.gz",
    ];

    protected abstract string VectorFile { get; }
    public string FileForGuard => VectorFile;

    // The flags-mask metadata, loaded once (skip-tolerant: M8088Metadata.Empty when the vectors are absent).
    private static readonly M8088Metadata s_metadata =
        M8088Metadata.Load(M8088TomHarteVectors.TryGetVectorDirectory());

    [M8088TomHarteFact]
    public void Mov_family_is_TomHarte_green_on_the_data_axis()
    {
        string file = VectorFile;
        string? dir = M8088TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);   // skipped at discovery when vectors are absent; present == not null
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"in-scope MOV-family vector file missing: {path}");

        string opcodeHex = file[..file.IndexOf('.')];   // strip ".json.gz" → the opcode hex ("88", "A0", ...)
        var cases = M8088TomHarteLoader.LoadFile(path);
        var failures = new List<string>();
        int sampleSize = M8088TomHarteVectors.ResolveSampleSize();
        int run = 0;
        int executed = 0;
        foreach (var c in cases)
        {
            if (run >= sampleSize) break;
            run++;
            executed++;
            string? r = M8088TomHarteRunner.RunCase(c, s_metadata, opcodeHex);   // data axis (Step + diff)
            if (r is not null)
            {
                failures.Add(r);
                if (failures.Count >= 10) break;   // cap the report; 10 failures is enough signal
            }
        }

        Assert.True(executed > 0, $"{file}: 0 executed cases — the gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ data-axis failures over {executed} executed cases:\n" +
            string.Join("\n", failures));
    }
}

public sealed class M8088Mov_88 : M8088MovTomHarteSweepBase { protected override string VectorFile => "88.json.gz"; }
public sealed class M8088Mov_89 : M8088MovTomHarteSweepBase { protected override string VectorFile => "89.json.gz"; }
public sealed class M8088Mov_8A : M8088MovTomHarteSweepBase { protected override string VectorFile => "8A.json.gz"; }
public sealed class M8088Mov_8B : M8088MovTomHarteSweepBase { protected override string VectorFile => "8B.json.gz"; }
public sealed class M8088Mov_8C : M8088MovTomHarteSweepBase { protected override string VectorFile => "8C.json.gz"; }
public sealed class M8088Mov_8E : M8088MovTomHarteSweepBase { protected override string VectorFile => "8E.json.gz"; }
public sealed class M8088Mov_A0 : M8088MovTomHarteSweepBase { protected override string VectorFile => "A0.json.gz"; }
public sealed class M8088Mov_A1 : M8088MovTomHarteSweepBase { protected override string VectorFile => "A1.json.gz"; }
public sealed class M8088Mov_A2 : M8088MovTomHarteSweepBase { protected override string VectorFile => "A2.json.gz"; }
public sealed class M8088Mov_A3 : M8088MovTomHarteSweepBase { protected override string VectorFile => "A3.json.gz"; }
public sealed class M8088Mov_B0 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B0.json.gz"; }
public sealed class M8088Mov_B1 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B1.json.gz"; }
public sealed class M8088Mov_B2 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B2.json.gz"; }
public sealed class M8088Mov_B3 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B3.json.gz"; }
public sealed class M8088Mov_B4 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B4.json.gz"; }
public sealed class M8088Mov_B5 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B5.json.gz"; }
public sealed class M8088Mov_B6 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B6.json.gz"; }
public sealed class M8088Mov_B7 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B7.json.gz"; }
public sealed class M8088Mov_B8 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B8.json.gz"; }
public sealed class M8088Mov_B9 : M8088MovTomHarteSweepBase { protected override string VectorFile => "B9.json.gz"; }
public sealed class M8088Mov_BA : M8088MovTomHarteSweepBase { protected override string VectorFile => "BA.json.gz"; }
public sealed class M8088Mov_BB : M8088MovTomHarteSweepBase { protected override string VectorFile => "BB.json.gz"; }
public sealed class M8088Mov_BC : M8088MovTomHarteSweepBase { protected override string VectorFile => "BC.json.gz"; }
public sealed class M8088Mov_BD : M8088MovTomHarteSweepBase { protected override string VectorFile => "BD.json.gz"; }
public sealed class M8088Mov_BE : M8088MovTomHarteSweepBase { protected override string VectorFile => "BE.json.gz"; }
public sealed class M8088Mov_BF : M8088MovTomHarteSweepBase { protected override string VectorFile => "BF.json.gz"; }
public sealed class M8088Mov_C6 : M8088MovTomHarteSweepBase { protected override string VectorFile => "C6.json.gz"; }
public sealed class M8088Mov_C7 : M8088MovTomHarteSweepBase { protected override string VectorFile => "C7.json.gz"; }

/// <summary>Structural guard: the per-file derived classes cover EXACTLY the canonical MOV file list.</summary>
public sealed class M8088MovTomHarteCoverageGuard
{
    [Fact]
    public void Split_classes_cover_exactly_the_canonical_mov_file_list()
    {
        var expected = M8088MovTomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
        var covered = typeof(M8088MovTomHarteSweepBase).Assembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && typeof(M8088MovTomHarteSweepBase).IsAssignableFrom(t))
            .Select(t => ((M8088MovTomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
            .OrderBy(x => x).ToArray();
        Assert.Equal(expected, covered);
    }
}
