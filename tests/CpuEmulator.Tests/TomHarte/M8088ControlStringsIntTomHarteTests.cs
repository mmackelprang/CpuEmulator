using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M5.5d — the 8086/8088 control-flow + strings/REP + IN/OUT + interrupt TomHarte green sweep: the un-fakeable,
/// silicon-derived ground-truth gate for the FINAL family group that COMPLETES the 8086 interpreter (data axis).
/// Runs every case (up to the per-file sample cap) of each in-scope opcode file through the real
/// <see cref="M8088TomHarteRunner.RunCase"/> Step + diff and asserts the DATA axis (the 14 registers + the changed
/// RAM cells; FLAGS mask-aware via the per-opcode / per-reg flags-mask).
///
/// <para><b>Coverage.</b> The conditional jumps (70-7F), the loops (E0-E3), the direct jumps/calls/returns
/// (E8/E9/EB near, 9A/EA far, C2/C3/CA/CB RET/RETF), the FF /2../5 indirect CALL/JMP group (per-reg files
/// FF.2-FF.5), the interrupts (CC INT3 / CD INT n / CE INTO / CF IRET — INCLUDING the IVT push sequence the
/// divide-error re-enable shares), the string ops (A4-AF MOVS/CMPS/SCAS/LODS/STOS byte+word, each file mixing
/// plain + REP/REPE/REPNE cases the body drives from the captured repeat prefix), and port I/O (E4-E7 imm8,
/// EC-EF DX). <b>No deferrals in this sweep</b> — every in-scope case goes fully green (the divide-error → INT0
/// re-enable + its disclosed DD6 undefined-flag deferral lives in the M5.5b ALU sweep, where the F6/F7/D4 files
/// are). If a file is absent from the local cache, the per-file assertion is skipped.</para>
///
/// <para><b>The reg-subfield seam.</b> The FF-group files are PER-REG (<c>FF.2.json.gz</c>, …); the sweep parses
/// the opcode hex as the segment before the first '.' AND extracts the reg subfield from the <c>NN.R.json.gz</c>
/// name, passing it as <c>regField</c> so the per-subgroup flags-mask is selected. A plain file passes
/// regField=null.</para>
///
/// <para>Parallelism: each in-scope file gets its OWN derived class (its own xUnit collection), the
/// M8088AluTomHarteTests pattern. <see cref="CanonicalFiles"/> is the source of truth; the coverage guard asserts
/// exact coverage. Skip-when-absent; the milestone gate runs CPUEMULATOR_UAT=full.</para>
/// </summary>
public abstract class M8088ControlStringsIntTomHarteSweepBase
{
    /// <summary>Every in-scope M5.5d opcode file (hex-keyed gzip). Plain files are <c>NN.json.gz</c>; the FF
    /// indirect group files are PER-REG <c>FF.R.json.gz</c>.</summary>
    public static readonly string[] CanonicalFiles = BuildCanonicalFiles();

    private static string[] BuildCanonicalFiles()
    {
        var files = new List<string>();

        // ── Conditional jumps (70-7F). ──────────────────────────────────────────────────────────────────────
        for (int op = 0x70; op <= 0x7F; op++) files.Add($"{op:X2}.json.gz");

        // ── Loops (E0-E3) + direct jumps/calls/returns. ───────────────────────────────────────────────────────
        foreach (var op in new[] { 0xE0, 0xE1, 0xE2, 0xE3 }) files.Add($"{op:X2}.json.gz");
        foreach (var op in new[] { 0xE8, 0xE9, 0xEB, 0x9A, 0xEA, 0xC2, 0xC3, 0xCA, 0xCB }) files.Add($"{op:X2}.json.gz");

        // ── FF /2../5 indirect CALL/JMP group (per-reg). (/0 /1 INC/DEC are M5.5b; /6 PUSH is M5.5c; /7 is
        //    undefined.) ───────────────────────────────────────────────────────────────────────────────────────
        foreach (var reg in new[] { 2, 3, 4, 5 }) files.Add($"FF.{reg}.json.gz");

        // ── Interrupts (CC INT3 / CD INT n / CE INTO / CF IRET). ─────────────────────────────────────────────
        foreach (var op in new[] { 0xCC, 0xCD, 0xCE, 0xCF }) files.Add($"{op:X2}.json.gz");

        // ── String ops (A4-AF). Each file mixes plain + REP-prefixed cases. ──────────────────────────────────
        foreach (var op in new[] { 0xA4, 0xA5, 0xA6, 0xA7, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF }) files.Add($"{op:X2}.json.gz");

        // ── Port I/O (E4-E7 imm8, EC-EF DX). ────────────────────────────────────────────────────────────────
        foreach (var op in new[] { 0xE4, 0xE5, 0xE6, 0xE7, 0xEC, 0xED, 0xEE, 0xEF }) files.Add($"{op:X2}.json.gz");

        return files.ToArray();
    }

    protected abstract string VectorFile { get; }
    public string FileForGuard => VectorFile;

    private static readonly M8088Metadata s_metadata =
        M8088Metadata.Load(M8088TomHarteVectors.TryGetVectorDirectory());

    [M8088TomHarteFact]
    public void ControlStringsInt_family_is_TomHarte_green_on_the_data_axis()
    {
        string file = VectorFile;
        string? dir = M8088TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;

        int firstDot = file.IndexOf('.');
        string opcodeHex = file[..firstDot];
        int? regField = null;
        string rest = file[(firstDot + 1)..];
        int restDot = rest.IndexOf('.');
        string firstSeg = restDot >= 0 ? rest[..restDot] : rest;
        if (int.TryParse(firstSeg, out int rf)) regField = rf;

        int sampleSize = M8088TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M8088.Get(path, sampleSize,
            max => M8088TomHarteLoader.LoadFile(path, max, parseCycles: false));   // data axis: skip carried cycles
        var failures = new List<string>();
        int run = 0, executed = 0;
        foreach (var c in cases)
        {
            if (run >= sampleSize) break;
            run++;
            executed++;
            string? res = M8088TomHarteRunner.RunCase(c, s_metadata, opcodeHex, regField);
            if (res is not null)
            {
                failures.Add(res);
                if (failures.Count >= 10) break;
            }
        }

        Assert.True(executed > 0, $"{file}: 0 executed cases — the gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ data-axis failures over {executed} executed cases:\n" +
            string.Join("\n", failures));
    }
}

// ── One sealed class per canonical file (the per-file xUnit collection). ─────────────────────────────────
// Conditional jumps 70-7F.
public sealed class M8088Ctl_70 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "70.json.gz"; }
public sealed class M8088Ctl_71 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "71.json.gz"; }
public sealed class M8088Ctl_72 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "72.json.gz"; }
public sealed class M8088Ctl_73 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "73.json.gz"; }
public sealed class M8088Ctl_74 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "74.json.gz"; }
public sealed class M8088Ctl_75 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "75.json.gz"; }
public sealed class M8088Ctl_76 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "76.json.gz"; }
public sealed class M8088Ctl_77 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "77.json.gz"; }
public sealed class M8088Ctl_78 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "78.json.gz"; }
public sealed class M8088Ctl_79 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "79.json.gz"; }
public sealed class M8088Ctl_7A : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "7A.json.gz"; }
public sealed class M8088Ctl_7B : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "7B.json.gz"; }
public sealed class M8088Ctl_7C : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "7C.json.gz"; }
public sealed class M8088Ctl_7D : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "7D.json.gz"; }
public sealed class M8088Ctl_7E : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "7E.json.gz"; }
public sealed class M8088Ctl_7F : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "7F.json.gz"; }
// Loops + direct transfers.
public sealed class M8088Ctl_E0 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E0.json.gz"; }
public sealed class M8088Ctl_E1 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E1.json.gz"; }
public sealed class M8088Ctl_E2 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E2.json.gz"; }
public sealed class M8088Ctl_E3 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E3.json.gz"; }
public sealed class M8088Ctl_E8 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E8.json.gz"; }
public sealed class M8088Ctl_E9 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E9.json.gz"; }
public sealed class M8088Ctl_EB : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "EB.json.gz"; }
public sealed class M8088Ctl_9A : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "9A.json.gz"; }
public sealed class M8088Ctl_EA : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "EA.json.gz"; }
public sealed class M8088Ctl_C2 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "C2.json.gz"; }
public sealed class M8088Ctl_C3 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "C3.json.gz"; }
public sealed class M8088Ctl_CA : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "CA.json.gz"; }
public sealed class M8088Ctl_CB : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "CB.json.gz"; }
// FF /2../5 indirect CALL/JMP group.
public sealed class M8088Ctl_FF_2 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "FF.2.json.gz"; }
public sealed class M8088Ctl_FF_3 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "FF.3.json.gz"; }
public sealed class M8088Ctl_FF_4 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "FF.4.json.gz"; }
public sealed class M8088Ctl_FF_5 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "FF.5.json.gz"; }
// Interrupts.
public sealed class M8088Int_CC : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "CC.json.gz"; }
public sealed class M8088Int_CD : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "CD.json.gz"; }
public sealed class M8088Int_CE : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "CE.json.gz"; }
public sealed class M8088Int_CF : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "CF.json.gz"; }
// String ops.
public sealed class M8088Str_A4 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "A4.json.gz"; }
public sealed class M8088Str_A5 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "A5.json.gz"; }
public sealed class M8088Str_A6 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "A6.json.gz"; }
public sealed class M8088Str_A7 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "A7.json.gz"; }
public sealed class M8088Str_AA : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "AA.json.gz"; }
public sealed class M8088Str_AB : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "AB.json.gz"; }
public sealed class M8088Str_AC : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "AC.json.gz"; }
public sealed class M8088Str_AD : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "AD.json.gz"; }
public sealed class M8088Str_AE : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "AE.json.gz"; }
public sealed class M8088Str_AF : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "AF.json.gz"; }
// Port I/O.
public sealed class M8088Io_E4 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E4.json.gz"; }
public sealed class M8088Io_E5 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E5.json.gz"; }
public sealed class M8088Io_E6 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E6.json.gz"; }
public sealed class M8088Io_E7 : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "E7.json.gz"; }
public sealed class M8088Io_EC : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "EC.json.gz"; }
public sealed class M8088Io_ED : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "ED.json.gz"; }
public sealed class M8088Io_EE : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "EE.json.gz"; }
public sealed class M8088Io_EF : M8088ControlStringsIntTomHarteSweepBase { protected override string VectorFile => "EF.json.gz"; }

/// <summary>Structural guard: the per-file derived classes cover EXACTLY the canonical M5.5d file list.</summary>
public sealed class M8088ControlStringsIntTomHarteCoverageGuard
{
    [Fact]
    public void Split_classes_cover_exactly_the_canonical_control_strings_int_file_list()
    {
        var expected = M8088ControlStringsIntTomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
        var covered = typeof(M8088ControlStringsIntTomHarteSweepBase).Assembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && typeof(M8088ControlStringsIntTomHarteSweepBase).IsAssignableFrom(t))
            .Select(t => ((M8088ControlStringsIntTomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
            .OrderBy(x => x).ToArray();
        Assert.Equal(expected, covered);
    }
}
