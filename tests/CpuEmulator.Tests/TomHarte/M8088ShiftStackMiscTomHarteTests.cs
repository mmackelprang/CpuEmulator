using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M5.5c — the 8086/8088 shift/rotate + stack + misc-data-movement TomHarte green sweep: the un-fakeable,
/// silicon-derived ground-truth gate for the shift/rotate flag core, the SS:SP stack discipline (incl. the
/// PUSH SP / POP SP / POPF reserved-bit quirks), and the misc ops (XCHG/LEA/LDS/LES/XLAT/LAHF/SAHF/CBW/CWD +
/// the flag-control set). Runs every case (up to the per-file sample cap) of each in-scope opcode file through
/// the real <see cref="M8088TomHarteRunner.RunCase"/> Step + diff and asserts the DATA axis (the 14 registers +
/// the changed RAM cells; FLAGS mask-aware via the per-opcode / per-reg flags-mask).
///
/// <para><b>No deferrals.</b> Unlike M5.5b (which deferred the divide-error/INT0 class and the IDIV sign-quirk),
/// M5.5c has NO genuinely-resistant case-class: the documented-undefined shift OF-for-count&gt;1 cases are
/// handled exactly (SHR count&gt;1 ⇒ OF=0; SHL/rotates compute OF from the result for every count — reconciled
/// byte-exact against the D0-D3 corpus), and the stack quirks are modeled precisely. Every in-scope, present
/// file goes fully green. If a file is absent from the local cache, the per-file assertion is skipped (the
/// canonical list is broad; the milestone gate runs with all vectors fetched).</para>
///
/// <para><b>The reg-subfield seam.</b> The shift group files (<c>D0.0.json.gz</c>, …) and the two stack-group
/// files (<c>8F.0</c> POP r/m16, <c>FF.6</c> PUSH r/m16) are PER-REG files; the sweep parses the opcode hex as
/// the segment before the first '.' AND extracts the reg subfield from the <c>NN.R.json.gz</c> name, passing it
/// as <c>regField</c> so the per-subgroup flags-mask is selected. A plain file passes regField=null.</para>
///
/// <para>Parallelism: each in-scope file gets its OWN derived class (its own xUnit collection), the
/// M8088AluTomHarteTests pattern. <see cref="CanonicalFiles"/> is the source of truth; the coverage guard
/// asserts exact coverage. Skip-when-absent; the milestone gate runs CPUEMULATOR_UAT=full.</para>
/// </summary>
public abstract class M8088ShiftStackMiscTomHarteSweepBase
{
    /// <summary>Every in-scope shift/stack/misc opcode file (hex-keyed gzip). Shift-group files are PER-REG
    /// <c>NN.R.json.gz</c>; the rest are plain <c>NN.json.gz</c> (plus the two stack-group files 8F.0, FF.6).</summary>
    public static readonly string[] CanonicalFiles = BuildCanonicalFiles();

    private static string[] BuildCanonicalFiles()
    {
        var files = new List<string>();

        // ── Shift/rotate group D0-D3, reg 0..5 + 7 (reg 6 is the undocumented SHL-alias — no Insn row,
        //    routes to undefined; out of M5.5c scope). ──────────────────────────────────────────────────────
        foreach (var op in new[] { 0xD0, 0xD1, 0xD2, 0xD3 })
            foreach (var reg in new[] { 0, 1, 2, 3, 4, 5, 7 })
                files.Add($"{op:X2}.{reg}.json.gz");

        // ── Stack: PUSH/POP r16 (50-5F), the segment PUSH/POP (06/07/0E/16/17/1E/1F), the group forms (8F.0
        //    POP r/m16, FF.6 PUSH r/m16), and PUSHF/POPF (9C/9D). (FF.0/FF.1 INC/DEC are M5.5b's; FF.2..5
        //    CALL/JMP are M5.5d.) ──────────────────────────────────────────────────────────────────────────
        for (int op = 0x50; op <= 0x5F; op++) files.Add($"{op:X2}.json.gz");
        foreach (var op in new[] { 0x06, 0x07, 0x0E, 0x16, 0x17, 0x1E, 0x1F }) files.Add($"{op:X2}.json.gz");
        // 8F POP r/m16 is an UNSPLIT file in the corpus (the reg field is a don't-care — it carries reg 0 + 1
        // cases in one 8F.json.gz, unlike the heterogeneous 80/81/F6 groups which split per reg).
        files.Add("8F.json.gz");
        files.Add("FF.6.json.gz");
        files.Add("9C.json.gz");
        files.Add("9D.json.gz");

        // ── Misc data-movement: XCHG (86/87 r/m,r ; 91-97 reg,AX), NOP (90), LEA (8D), LES/LDS (C4/C5),
        //    XLAT (D7), SAHF/LAHF (9E/9F), CBW/CWD (98/99). ───────────────────────────────────────────────────
        foreach (var op in new[] { 0x86, 0x87 }) files.Add($"{op:X2}.json.gz");
        for (int op = 0x90; op <= 0x99; op++) files.Add($"{op:X2}.json.gz");   // 90 NOP, 91-97 XCHG, 98 CBW, 99 CWD
        files.Add("9E.json.gz");   // SAHF
        files.Add("9F.json.gz");   // LAHF
        files.Add("8D.json.gz");   // LEA
        files.Add("C4.json.gz");   // LES
        files.Add("C5.json.gz");   // LDS
        files.Add("D7.json.gz");   // XLAT

        // ── Flag-control + HLT/WAIT. CLC/STC (F8/F9), CLI/STI (FA/FB), CLD/STD (FC/FD), CMC (F5), HLT (F4),
        //    WAIT (9B). ─────────────────────────────────────────────────────────────────────────────────────
        foreach (var op in new[] { 0xF4, 0xF5, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD }) files.Add($"{op:X2}.json.gz");
        files.Add("9B.json.gz");   // WAIT

        return files.ToArray();
    }

    protected abstract string VectorFile { get; }
    public string FileForGuard => VectorFile;

    private static readonly M8088Metadata s_metadata =
        M8088Metadata.Load(M8088TomHarteVectors.TryGetVectorDirectory());

    [M8088TomHarteFact]
    public void ShiftStackMisc_family_is_TomHarte_green_on_the_data_axis()
    {
        string file = VectorFile;
        string? dir = M8088TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        // Skip-when-absent for an individual file (the canonical list is broad; a missing file is tolerated —
        // the milestone gate runs with all vectors fetched).
        if (!File.Exists(path)) return;

        // Parse opcode hex = segment before the FIRST '.', and the reg subfield from a NN.R.json.gz name.
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
// Shift/rotate group (D0-D3 reg 0..5,7).
public sealed class M8088Shift_D0_0 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D0.0.json.gz"; }
public sealed class M8088Shift_D0_1 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D0.1.json.gz"; }
public sealed class M8088Shift_D0_2 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D0.2.json.gz"; }
public sealed class M8088Shift_D0_3 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D0.3.json.gz"; }
public sealed class M8088Shift_D0_4 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D0.4.json.gz"; }
public sealed class M8088Shift_D0_5 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D0.5.json.gz"; }
public sealed class M8088Shift_D0_7 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D0.7.json.gz"; }
public sealed class M8088Shift_D1_0 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D1.0.json.gz"; }
public sealed class M8088Shift_D1_1 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D1.1.json.gz"; }
public sealed class M8088Shift_D1_2 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D1.2.json.gz"; }
public sealed class M8088Shift_D1_3 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D1.3.json.gz"; }
public sealed class M8088Shift_D1_4 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D1.4.json.gz"; }
public sealed class M8088Shift_D1_5 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D1.5.json.gz"; }
public sealed class M8088Shift_D1_7 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D1.7.json.gz"; }
public sealed class M8088Shift_D2_0 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D2.0.json.gz"; }
public sealed class M8088Shift_D2_1 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D2.1.json.gz"; }
public sealed class M8088Shift_D2_2 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D2.2.json.gz"; }
public sealed class M8088Shift_D2_3 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D2.3.json.gz"; }
public sealed class M8088Shift_D2_4 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D2.4.json.gz"; }
public sealed class M8088Shift_D2_5 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D2.5.json.gz"; }
public sealed class M8088Shift_D2_7 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D2.7.json.gz"; }
public sealed class M8088Shift_D3_0 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D3.0.json.gz"; }
public sealed class M8088Shift_D3_1 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D3.1.json.gz"; }
public sealed class M8088Shift_D3_2 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D3.2.json.gz"; }
public sealed class M8088Shift_D3_3 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D3.3.json.gz"; }
public sealed class M8088Shift_D3_4 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D3.4.json.gz"; }
public sealed class M8088Shift_D3_5 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D3.5.json.gz"; }
public sealed class M8088Shift_D3_7 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D3.7.json.gz"; }
// Stack: PUSH/POP r16.
public sealed class M8088Stack_50 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "50.json.gz"; }
public sealed class M8088Stack_51 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "51.json.gz"; }
public sealed class M8088Stack_52 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "52.json.gz"; }
public sealed class M8088Stack_53 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "53.json.gz"; }
public sealed class M8088Stack_54 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "54.json.gz"; }
public sealed class M8088Stack_55 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "55.json.gz"; }
public sealed class M8088Stack_56 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "56.json.gz"; }
public sealed class M8088Stack_57 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "57.json.gz"; }
public sealed class M8088Stack_58 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "58.json.gz"; }
public sealed class M8088Stack_59 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "59.json.gz"; }
public sealed class M8088Stack_5A : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "5A.json.gz"; }
public sealed class M8088Stack_5B : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "5B.json.gz"; }
public sealed class M8088Stack_5C : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "5C.json.gz"; }
public sealed class M8088Stack_5D : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "5D.json.gz"; }
public sealed class M8088Stack_5E : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "5E.json.gz"; }
public sealed class M8088Stack_5F : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "5F.json.gz"; }
// Stack: segment PUSH/POP.
public sealed class M8088Stack_06 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "06.json.gz"; }
public sealed class M8088Stack_07 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "07.json.gz"; }
public sealed class M8088Stack_0E : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "0E.json.gz"; }
public sealed class M8088Stack_16 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "16.json.gz"; }
public sealed class M8088Stack_17 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "17.json.gz"; }
public sealed class M8088Stack_1E : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "1E.json.gz"; }
public sealed class M8088Stack_1F : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "1F.json.gz"; }
// Stack: group POP/PUSH r/m16 + PUSHF/POPF.
public sealed class M8088Stack_8F : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "8F.json.gz"; }
public sealed class M8088Stack_FF_6 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "FF.6.json.gz"; }
public sealed class M8088Stack_9C : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "9C.json.gz"; }
public sealed class M8088Stack_9D : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "9D.json.gz"; }
// Misc data-movement.
public sealed class M8088Misc_86 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "86.json.gz"; }
public sealed class M8088Misc_87 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "87.json.gz"; }
public sealed class M8088Misc_90 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "90.json.gz"; }
public sealed class M8088Misc_91 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "91.json.gz"; }
public sealed class M8088Misc_92 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "92.json.gz"; }
public sealed class M8088Misc_93 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "93.json.gz"; }
public sealed class M8088Misc_94 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "94.json.gz"; }
public sealed class M8088Misc_95 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "95.json.gz"; }
public sealed class M8088Misc_96 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "96.json.gz"; }
public sealed class M8088Misc_97 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "97.json.gz"; }
public sealed class M8088Misc_98 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "98.json.gz"; }
public sealed class M8088Misc_99 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "99.json.gz"; }
public sealed class M8088Misc_9E : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "9E.json.gz"; }
public sealed class M8088Misc_9F : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "9F.json.gz"; }
public sealed class M8088Misc_8D : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "8D.json.gz"; }
public sealed class M8088Misc_C4 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "C4.json.gz"; }
public sealed class M8088Misc_C5 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "C5.json.gz"; }
public sealed class M8088Misc_D7 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "D7.json.gz"; }
// Flag-control + HLT/WAIT.
public sealed class M8088Misc_F4 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "F4.json.gz"; }
public sealed class M8088Misc_F5 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "F5.json.gz"; }
public sealed class M8088Misc_F8 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "F8.json.gz"; }
public sealed class M8088Misc_F9 : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "F9.json.gz"; }
public sealed class M8088Misc_FA : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "FA.json.gz"; }
public sealed class M8088Misc_FB : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "FB.json.gz"; }
public sealed class M8088Misc_FC : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "FC.json.gz"; }
public sealed class M8088Misc_FD : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "FD.json.gz"; }
public sealed class M8088Misc_9B : M8088ShiftStackMiscTomHarteSweepBase { protected override string VectorFile => "9B.json.gz"; }

/// <summary>Structural guard: the per-file derived classes cover EXACTLY the canonical shift/stack/misc list.</summary>
public sealed class M8088ShiftStackMiscTomHarteCoverageGuard
{
    [Fact]
    public void Split_classes_cover_exactly_the_canonical_shift_stack_misc_file_list()
    {
        var expected = M8088ShiftStackMiscTomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
        var covered = typeof(M8088ShiftStackMiscTomHarteSweepBase).Assembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && typeof(M8088ShiftStackMiscTomHarteSweepBase).IsAssignableFrom(t))
            .Select(t => ((M8088ShiftStackMiscTomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
            .OrderBy(x => x).ToArray();
        Assert.Equal(expected, covered);
    }
}
