using System.Collections.Generic;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.6 headline gate: the 680x0 SingleStepTests DATA-AXIS sweep run THROUGH JittedCpu&lt;M68000Cpu&gt;
/// (all-fallback). Every 68000 op falls back to inner.Step in M4, so the JIT final state MUST equal the
/// interpreter's (which already passes these vectors — M4.5a–d-1). A green sweep proves the GENERIC COMPILER
/// (the discovery walk, the keyed DescriptorFor, the per-CPU BlockDelegate, the data-driven register map, the
/// cycle/budget/dispatch machinery) runs the complete 68000 faithfully — the same proof 5-3a delivered for the
/// Z80, now on the 32-bit-register / 24-bit-address / big-endian / word-decode CPU. The data axis is
/// D0–D7/A0–A6/USP/SSP/SR/RAM (fastmem bypasses the per-transaction bus trace, the same scope the 6502/Z80 JIT
/// sweeps assert). The TIMING axis (final.pc/prefetch/trace/cycle) is gated on M4.5d-2 and is NOT asserted here.
/// Exception cases assert via assertExceptions:true (the synchronous vector is handled by the fallback valve);
/// the address-error (vector 3) large frame stays deferred (DD4). The corpus-artifact cases the interpreter
/// sweeps exclude (the ASL.b inconsistent-register-shift vectors + the CHK in-range UNPREDICTABLE-CCR cases)
/// are excluded identically here via M68000DataAxisCorpus.IsExcludedCase, so the JIT corpus is identical in
/// EXECUTED cases to the interpreter corpus. Sampled at CI scale; CPUEMULATOR_UAT=full runs every case through
/// the JIT.
///
/// <para>Lever-3 split: the sweep is one xUnit COLLECTION per partition (the 8 sealed M68000JitTom_P0..P7
/// derived classes below), so the heaviest JIT tier parallelizes across the configured threads, mirroring the
/// interpreter split (Mos6502TomHarteSweepBase). The exclusion + deferral logic is IDENTICAL to the pre-split
/// single-class body.</para></summary>
public abstract class M68000JitSweepBase(ITestOutputHelper output)
{
    /// <summary>Partition the data-axis file list into <paramref name="parts"/> stripes; return stripe
    /// <paramref name="index"/>. Stripe assignment is by position (i % parts) so each stripe is a balanced mix.</summary>
    public static TheoryData<string> Partition(int index, int parts)
    {
        var data = new TheoryData<string>();
        int i = 0;
        foreach (var f in M68000DataAxisCorpus.Files) { if (i % parts == index) data.Add(f); i++; }
        return data;
    }

    protected void RunFile(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"vector file missing: {path}");

        int sample = M68000TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M68000.Get(path, sample,
            max => M68000TomHarteLoader.LoadFile(path, max));
        int run = 0, executed = 0, deferred = 0, excluded = 0;
        var failures = new List<string>();
        foreach (var c in cases)
        {
            if (run >= sample) break;
            run++;
            // Carry the interpreter data-axis sweeps' corpus-artifact exclusions forward (Refinement 3): the
            // ASL.b inconsistent-register-shift vectors + the CHK in-range UNPREDICTABLE-CCR cases. Because
            // RunCaseThroughJit runs the same interpreter via the all-fallback valve, an excluded case would
            // produce the SAME "failure" the interpreter sweeps avoid by skipping. Excluded BEFORE the run.
            if (M68000DataAxisCorpus.IsExcludedCase(c)) { excluded++; continue; }
            var rr = M68000TomHarteRunner.RunCaseThroughJit(c, assertExceptions: true);
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 5) break; }
        }
        output.WriteLine($"{file}: ran {run}, executed {executed}, deferred {deferred}, excluded {excluded} (68000 JIT)");
        Assert.True(executed > 0, $"{file}: 0 executed cases — the gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count} tier-parity failure(s) of {executed} executed:\n" +
            string.Join("\n---\n", failures));
    }
}

public sealed class M68000JitTom_P0(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(0, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P1(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(1, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P2(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(2, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P3(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(3, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P4(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(4, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P5(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(5, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P6(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(6, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P7(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(7, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

/// <summary>M6 PR-4 (Task 7): the focused MOVE/MOVEA/MOVEQ data-axis parity sweep. These six files
/// (MOVE.b/.w/.l, MOVEA.w/.l, MOVE.q == MOVEQ) are the families PR-4 generated descriptor rows + an emit arm for.
/// Run through <see cref="M68000TomHarteRunner.RunCaseThroughJit"/>, the JIT final state (D0–D7/A0–A6/USP/SSP/SR/RAM)
/// is byte-identical to the interpreter for every non-exception case. NOT cycle/pc/prefetch (DECISION T2).
///
/// <para>M6 PR-4a made this a REAL emitted-IL gate. BlockCompiler.Discover now feeds the 68000 a WORD-granular
/// M68000FetchStream (UnitBytes==2), so the generated Decode() matches the operword, DescriptorFor returns the
/// real MOVE/MOVEA/MOVEQ rows, and EmitM68kMove DISPATCHES at runtime (proven by the committed
/// M68kMoveEmitSelections &gt; 0 counter — M68000JitGenericityTests.M68000_MOVE_arm_actually_dispatches_after_PR4a).
/// So this sweep now diffs the emitted IL against the interpreter oracle for every executed case — load-bearing,
/// NOT interpreter-vs-interpreter.</para>
///
/// <para><b>GREEN — full EA matrix, byte-identical (PR-4b).</b> Every EA mode — Dn/An direct, (An), (An)+,
/// -(An) (A0-A6 AND A7), d16(An), d8(An,Xn), abs.w/abs.l, d16(PC)/d8(PC,Xn)/#imm sources — is byte-identical on the
/// data axis (D0-D7/A0-A6/USP/SSP/SR/RAM). PR-4b fixed the two emit-arm defects PR-4a surfaced: (1) the EA mode 5
/// <c>d16(An)</c> path passed its sign-extended displacement to <c>ILGenerator.Emit</c> as a <c>short</c>, which
/// silently bound to the <c>Emit(OpCode, Int16)</c> overload and emitted a truncated <c>Ldc_I4</c> operand
/// (InvalidProgramException at execute); it now sign-extends to <c>int</c> (EmitAddDisp16). (2) The wide-store /
/// byte-store page derivation (<c>ea &gt;&gt; 8</c>) did not clamp the resolved EA to the bus address width, so a
/// MOVE dest whose full-32-bit A-register-relative address carried bits above the 24-bit bus overran the
/// fastmem/DirtyMap page arrays (IndexOutOfRangeException — mis-attributed to "A7" in the PR-4a notes; A7 banking
/// was always correct); the page derivation now masks with <c>_bus.AddressMask</c> (EmitMaskAddrLocalToBus /
/// EmitMaskEaLocalToBus). This sweep is a load-bearing merge gate.</para></summary>
public sealed class M68000JitMoveFamilyTests(ITestOutputHelper output)
{
    public static TheoryData<string> MoveFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in new[]
        {
            "MOVE.b.json.gz", "MOVE.w.json.gz", "MOVE.l.json.gz",
            "MOVEA.w.json.gz", "MOVEA.l.json.gz", "MOVE.q.json.gz",   // MOVE.q = MOVEQ's vector file
        })
            data.Add(f);
        return data;
    }

    // M6 PR-4b: the REAL emitted-IL gate — LIVE (was Skip'd in PR-4a pending the two EA-helper fixes; both are now
    // fixed — see the class XML doc). The full EA-matrix sweep diffs the emitted MOVE IL against the interpreter
    // oracle for every executed case and is byte-identical on the data axis, including the d16(An) (src+dest) and
    // A7 edges that PR-4a surfaced as defective.
    [M68000TomHarteTheory]
    [MemberData(nameof(MoveFiles))]
    public void Move_family_emitted_IL_is_data_axis_parity_green(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine(dir, file);
        Assert.True(System.IO.File.Exists(path), $"MOVE-family vector file missing: {path}");

        int sample = M68000TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M68000.Get(path, sample,
            max => M68000TomHarteLoader.LoadFile(path, max));
        int executed = 0, deferred = 0, excluded = 0;
        var failures = new List<string>();
        foreach (var c in cases)
        {
            if (M68000DataAxisCorpus.IsExcludedCase(c)) { excluded++; continue; }
            var rr = M68000TomHarteRunner.RunCaseThroughJit(c, assertExceptions: true);
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 8) break; }
        }
        output.WriteLine($"{file}: executed {executed}, deferred {deferred}, excluded {excluded} (MOVE emitted-IL JIT)");
        Assert.True(executed > 0, $"{file}: 0 executed cases — the emitted-IL gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count} MOVE emitted-IL parity failure(s) of {executed} executed:\n" +
            string.Join("\n---\n", failures));
    }
}

/// <summary>M6 PR-5 (Task 6): the focused integer-ALU data-axis parity sweep — the X-bit gate. These files are the
/// PR-5 families that generated JitOpClass.M68000Alu rows + an emit arm (ADD/SUB/CMP/AND/OR/EOR RegEa, ADDA/SUBA/
/// CMPA AddrEa, ADDX/SUBX XAlu) at .b/.w/.l per the canonical list — NOT NEG/NEGX/NOT/CLR/TST/EXT/MUL/DIV (those
/// stay fallback), NOT the immediate/quick forms (no v1 vector files exist for ADDI/ADDQ/SUBI/SUBQ — see the
/// interpreter M68000AluTomHarteSweepBase HONESTY note; those forms ARE emitted, but the corpus exercises only
/// the RegEa/AddrEa/XAlu shapes). Run through <see cref="M68000TomHarteRunner.RunCaseThroughJit"/>, the JIT final
/// state (D0-D7/A0-A6/USP/SSP/SR/RAM) is byte-identical to the interpreter for every executed case.
///
/// <para><b>The SR comparison is the entire X-bit safety net.</b> It catches: X=C errors (ADD/SUB), X-INPUT
/// errors (ADDX/SUBX), X-restored errors (CMP/CMPA), and X-untouched errors (AND/OR/EOR — the reused Logic CCR).
/// The corpus stresses exactly the dominant failure class: the SUB-self-to-zero C=X=0 case (proving the xIn:false
/// hard-wire for plain SUB), the ADDX/SUBX sticky-Z + X-chain cases, the memory-dest RMW (An)+/-(An) An-mutation,
/// and the per-size mask/signbit. The ALU rows are M68000Alu-emitted IL (proven non-vacuous by the
/// M68kAluEmitSelections counter), so this diffs emitted IL vs the interpreter oracle, not interpreter-vs-
/// interpreter. NOT cycle/pc/prefetch (DECISION T2).</para></summary>
public sealed class M68000JitAluFamilyTests(ITestOutputHelper output)
{
    /// <summary>The integer-ALU vector files PR-5 emits (the in-corpus subset of the canonical ALU list — the
    /// immediate/quick forms have no v1 vectors, and NEG/NEGX/NOT/CLR/TST/EXT/MUL/DIV stay fallback).</summary>
    public static TheoryData<string> AluFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in new[]
        {
            "ADD.b.json.gz", "ADD.w.json.gz", "ADD.l.json.gz",
            "SUB.b.json.gz", "SUB.w.json.gz", "SUB.l.json.gz",
            "CMP.b.json.gz", "CMP.w.json.gz", "CMP.l.json.gz",
            "AND.b.json.gz", "AND.w.json.gz", "AND.l.json.gz",
            "OR.b.json.gz",  "OR.w.json.gz",  "OR.l.json.gz",
            "EOR.b.json.gz", "EOR.w.json.gz", "EOR.l.json.gz",
            "ADDA.w.json.gz", "ADDA.l.json.gz",
            "SUBA.w.json.gz", "SUBA.l.json.gz",
            "CMPA.w.json.gz", "CMPA.l.json.gz",
            "ADDX.b.json.gz", "ADDX.w.json.gz", "ADDX.l.json.gz",
            "SUBX.b.json.gz", "SUBX.w.json.gz", "SUBX.l.json.gz",
        })
            data.Add(f);
        return data;
    }

    [M68000TomHarteTheory]
    [MemberData(nameof(AluFiles))]
    public void Alu_family_emitted_IL_is_data_axis_parity_green(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine(dir, file);
        Assert.True(System.IO.File.Exists(path), $"ALU-family vector file missing: {path}");

        int sample = M68000TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M68000.Get(path, sample,
            max => M68000TomHarteLoader.LoadFile(path, max));
        int executed = 0, deferred = 0, excluded = 0;
        var failures = new List<string>();
        foreach (var c in cases)
        {
            if (M68000DataAxisCorpus.IsExcludedCase(c)) { excluded++; continue; }
            var rr = M68000TomHarteRunner.RunCaseThroughJit(c, assertExceptions: true);
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 8) break; }
        }
        output.WriteLine($"{file}: executed {executed}, deferred {deferred}, excluded {excluded} (ALU emitted-IL JIT)");
        Assert.True(executed > 0, $"{file}: 0 executed cases — the emitted-IL X-bit gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count} ALU emitted-IL parity failure(s) of {executed} executed:\n" +
            string.Join("\n---\n", failures));
    }
}
