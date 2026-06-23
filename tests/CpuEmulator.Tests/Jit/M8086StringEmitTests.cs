using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ROADMAP #4 Row STR: the 8086 string family (MOVS/CMPS/STOS/LODS/SCAS, A4-A7/AA-AF, byte+word,
/// with or without a REP/REPE/REPNE prefix) is LIVE (the EmitM8086String arm) and DISPATCHES, and the
/// emitted registers + FLAGS + the copied/compared RAM cells are BYTE-IDENTICAL to the interpreter oracle
/// (M8086Cpu.String.cs StringExecute/StringStep). The headline byte-identity gate is the A4-AF files in
/// the M8088JitTom sweep; this file pins the non-vacuity (M8086StringEmitSelections &gt; 0,
/// FallbackEmitCount == 0) + the densest correctness pockets (both DF directions, the REP CX-loop, the
/// REPE/REPNE ZF early-exit, the CX=0 zero-iteration case), comparing a JIT run against a fresh
/// interpreter from the SAME initial state + seeded memory. The interpreter is the oracle.</summary>
public class M8086StringEmitTests
{
    private static (M8086Cpu Jit, AddressSpace JBus, M8086Cpu Interp, AddressSpace IBus) RunBoth(
        byte[] code, out int stringEmit, out int fallback,
        (string Name, ushort Value)[] seeds,
        params (uint PhysAddr, byte Value)[] seedMem)
    {
        const ushort cs = 0x1000, ip = 0x0000;
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);

        static void Seed(AddressSpace bus, byte[] code, uint phys, (uint, byte)[] seedMem)
        {
            for (int i = 0; i < code.Length; i++) bus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
            foreach (var (addr, value) in seedMem) bus.Write8(addr & 0xFFFFF, value);
        }

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        Seed(jbus, code, phys, seedMem);
        var jit = new M8086Cpu(jbus);
        jit.SetRegister("CS", cs); jit.SetRegister("IP", ip);
        foreach (var (n, v) in seeds) jit.SetRegister(n, v);
        var jc = new JittedCpu<M8086Cpu>(jit, M8086Cpu.JitTarget, jbus);
        long budget = 1; jc.Run(ref budget);
        stringEmit = jc.M8086StringEmitSelections;
        fallback = jc.FallbackEmitCount;

        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        Seed(ibus, code, phys, seedMem);
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip);
        foreach (var (n, v) in seeds) interp.SetRegister(n, v);
        interp.Step();
        return (jit, jbus, interp, ibus);
    }

    /// <summary>Assert the JIT and interpreter agree on the named registers, FLAGS, and a memcmp window of
    /// <paramref name="window"/> bytes starting at <paramref name="windowPhys"/> in each CPU's bus (the
    /// copied/stored string cells). The interpreter is the oracle.</summary>
    private static void AssertAgree(
        M8086Cpu jit, AddressSpace jbus, M8086Cpu interp, AddressSpace ibus,
        string[] regs, uint windowPhys, int window)
    {
        foreach (var r in regs)
            Assert.Equal(interp.GetRegister(r), jit.GetRegister(r));
        for (uint k = 0; k < window; k++)
            Assert.Equal(ibus.Read8((windowPhys + k) & 0xFFFFF), jbus.Read8((windowPhys + k) & 0xFFFFF));
    }

    // ───────────────────────────── Task 1: routing (red until EmitM8086String exists) ─────────────────────────────

    [Fact]
    public void Movsb_forward_emits_and_is_not_a_fallback()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // A4 MOVSB: ES:DI <- DS:SI, then SI++/DI++ (DF=0). Seed DS,ES,SI,DI + a source byte.
        var (jit, jbus, interp, ibus) = RunBoth([0xA4], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020)],
            (((0x2000u << 4) + 0x0010u), 0x5A));   // DS:SI = 0x5A
        Assert.True(emit > 0, "MOVSB was not emitted (the string arm never dispatched).");
        Assert.Equal(0, fb);
        // ES:DI window (the copied byte) + SI/DI (stepped +1).
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI"], (0x3000u << 4) + 0x0020u, 2);
    }

    // ───────────────────────────── Task 2: MOVS/STOS/LODS, both DF directions ─────────────────────────────

    [Fact]
    public void Movsw_forward_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // A5 MOVSW: copy a word DS:SI -> ES:DI, SI+=2/DI+=2 (DF=0).
        var (jit, jbus, interp, ibus) = RunBoth([0xA5], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020)],
            (((0x2000u << 4) + 0x0010u), 0x34), (((0x2000u << 4) + 0x0011u), 0x12));   // word 0x1234
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI"], (0x3000u << 4) + 0x0020u, 2);
    }

    [Fact]
    public void Movsb_backward_df_set_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // A4 MOVSB with DF=1 (FLAGS bit 10): SI--/DI-- after the copy.
        var (jit, jbus, interp, ibus) = RunBoth([0xA4], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020), ("FLAGS", (ushort)(0x0002 | (1 << 10)))],
            (((0x2000u << 4) + 0x0010u), 0x7E));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI", "FLAGS"], (0x3000u << 4) + 0x0020u, 1);
    }

    [Fact]
    public void Movsw_backward_df_set_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // A5 MOVSW with DF=1: SI-=2/DI-=2 after the word copy.
        var (jit, jbus, interp, ibus) = RunBoth([0xA5], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020), ("FLAGS", (ushort)(0x0002 | (1 << 10)))],
            (((0x2000u << 4) + 0x0010u), 0xCD), (((0x2000u << 4) + 0x0011u), 0xAB));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI", "FLAGS"], (0x3000u << 4) + 0x0020u, 2);
    }

    [Fact]
    public void Stosb_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // AA STOSB: ES:DI <- AL, DI++ (DF=0). AL=0x99.
        var (jit, jbus, interp, ibus) = RunBoth([0xAA], out int emit, out int fb,
            [("ES", 0x3000), ("DI", 0x0040), ("AX", 0x0099)]);
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["DI"], (0x3000u << 4) + 0x0040u, 1);
    }

    [Fact]
    public void Stosw_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // AB STOSW: ES:DI <- AX, DI+=2 (DF=0). AX=0xBEEF.
        var (jit, jbus, interp, ibus) = RunBoth([0xAB], out int emit, out int fb,
            [("ES", 0x3000), ("DI", 0x0040), ("AX", 0xBEEF)]);
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["DI"], (0x3000u << 4) + 0x0040u, 2);
    }

    [Fact]
    public void Lodsb_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // AC LODSB: AL <- DS:SI, SI++ (DF=0). Source byte 0x77.
        var (jit, jbus, interp, ibus) = RunBoth([0xAC], out int emit, out int fb,
            [("DS", 0x2000), ("SI", 0x0010), ("AX", 0x1100)],
            (((0x2000u << 4) + 0x0010u), 0x77));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "AX"], (0x2000u << 4) + 0x0010u, 0);
    }

    [Fact]
    public void Lodsw_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // AD LODSW: AX <- DS:SI, SI+=2 (DF=0). Source word 0xF00D.
        var (jit, jbus, interp, ibus) = RunBoth([0xAD], out int emit, out int fb,
            [("DS", 0x2000), ("SI", 0x0010), ("AX", 0x0000)],
            (((0x2000u << 4) + 0x0010u), 0x0D), (((0x2000u << 4) + 0x0011u), 0xF0));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "AX"], (0x2000u << 4) + 0x0010u, 0);
    }

    [Fact]
    public void Movsb_with_segment_override_uses_overridden_source()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // 26 A4 = ES: MOVSB. The SOURCE segment override-replaces DS with ES (the destination stays ES).
        // Seed the byte at ES:SI (the overridden source). Just prove JIT==interpreter (the override path).
        var (jit, jbus, interp, ibus) = RunBoth([0x26, 0xA4], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020)],
            (((0x3000u << 4) + 0x0010u), 0x42));   // ES:SI = 0x42 (the overridden source)
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI"], (0x3000u << 4) + 0x0020u, 1);
    }

    // ───────────────────────────── Task 3: CMPS/SCAS (flags-only) ─────────────────────────────

    [Fact]
    public void Cmpsb_equal_sets_zf_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // A6 CMPSB: compare DS:SI - ES:DI (equal bytes -> ZF=1). Flags + SI/DI byte-identical.
        var (jit, jbus, interp, ibus) = RunBoth([0xA6], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020)],
            (((0x2000u << 4) + 0x0010u), 0x55), (((0x3000u << 4) + 0x0020u), 0x55));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI", "FLAGS"], 0, 0);
    }

    [Fact]
    public void Cmpsb_unequal_clears_zf_full_subflags_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // A6 CMPSB with unequal bytes -> ZF=0, and the SUB-form CF/AF/OF/SF/PF byte-identical.
        var (jit, jbus, interp, ibus) = RunBoth([0xA6], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020)],
            (((0x2000u << 4) + 0x0010u), 0x10), (((0x3000u << 4) + 0x0020u), 0x30));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI", "FLAGS"], 0, 0);
    }

    [Fact]
    public void Scasw_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // AF SCASW: compare AX - ES:DI (flags only), DI+=2. AX=0x1234 vs the seeded ES:DI word.
        var (jit, jbus, interp, ibus) = RunBoth([0xAF], out int emit, out int fb,
            [("ES", 0x3000), ("DI", 0x0020), ("AX", 0x1234)],
            (((0x3000u << 4) + 0x0020u), 0x34), (((0x3000u << 4) + 0x0021u), 0x12));   // ES:DI = 0x1234 (equal)
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["DI", "FLAGS"], 0, 0);
    }

    // ───────────────────────────── Task 4: REP / REPE / REPNE ─────────────────────────────

    [Fact]
    public void Rep_movsb_copies_cx_bytes()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F3 A4 = REP MOVSB, CX=4 -> 4 bytes copied, CX=0, SI/DI advanced by 4.
        var (jit, jbus, interp, ibus) = RunBoth([0xF3, 0xA4], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020), ("CX", 0x0004)],
            (((0x2000u << 4) + 0x0010u), 0x11), (((0x2000u << 4) + 0x0011u), 0x22),
            (((0x2000u << 4) + 0x0012u), 0x33), (((0x2000u << 4) + 0x0013u), 0x44));
        Assert.True(emit > 0, "REP MOVSB was not emitted.");
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI", "CX"], (0x3000u << 4) + 0x0020u, 4);
    }

    [Fact]
    public void Repe_cmpsb_stops_on_first_mismatch()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F3 A6 = REPE CMPSB over a 4-byte run where byte index 2 differs -> stops AFTER comparing it.
        // CX stops at the exact interpreter value (not 0), SI/DI at the interpreter values, ZF=0.
        var (jit, jbus, interp, ibus) = RunBoth([0xF3, 0xA6], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020), ("CX", 0x0004)],
            (((0x2000u << 4) + 0x0010u), 0xAA), (((0x3000u << 4) + 0x0020u), 0xAA),   // equal
            (((0x2000u << 4) + 0x0011u), 0xBB), (((0x3000u << 4) + 0x0021u), 0xBB),   // equal
            (((0x2000u << 4) + 0x0012u), 0xCC), (((0x3000u << 4) + 0x0022u), 0xDD),   // MISMATCH
            (((0x2000u << 4) + 0x0013u), 0xEE), (((0x3000u << 4) + 0x0023u), 0xEE));  // equal (not reached)
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI", "CX", "FLAGS"], 0, 0);
    }

    [Fact]
    public void Repne_scasw_stops_on_match()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F2 AF = REPNE SCASW: repeat while ZF=0 (no match); stop when ZF=1 (a match). AX=0x1234; the run
        // has a match at the 2nd word. CX/DI/FLAGS at the exact interpreter values.
        var (jit, jbus, interp, ibus) = RunBoth([0xF2, 0xAF], out int emit, out int fb,
            [("ES", 0x3000), ("DI", 0x0020), ("AX", 0x1234), ("CX", 0x0004)],
            (((0x3000u << 4) + 0x0020u), 0x99), (((0x3000u << 4) + 0x0021u), 0x88),   // != 0x1234
            (((0x3000u << 4) + 0x0022u), 0x34), (((0x3000u << 4) + 0x0023u), 0x12));  // == 0x1234 (match)
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        AssertAgree(jit, jbus, interp, ibus, ["DI", "CX", "FLAGS"], 0, 0);
    }

    [Fact]
    public void Rep_with_cx_zero_does_nothing()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F3 A4 = REP MOVSB, CX=0 -> zero iterations, no register/memory change.
        var (jit, jbus, interp, ibus) = RunBoth([0xF3, 0xA4], out int emit, out int fb,
            [("DS", 0x2000), ("ES", 0x3000), ("SI", 0x0010), ("DI", 0x0020), ("CX", 0x0000)],
            (((0x2000u << 4) + 0x0010u), 0x5A));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        // Nothing copied: SI/DI/CX unchanged (the interpreter oracle is also unchanged), and the ES:DI cell
        // stays 0 (its seed). The non-mutation is pinned by the explicit ulong-typed equalities below.
        AssertAgree(jit, jbus, interp, ibus, ["SI", "DI", "CX"], (0x3000u << 4) + 0x0020u, 1);
        Assert.Equal((ulong)0x0010, jit.GetRegister("SI"));
        Assert.Equal((ulong)0x0020, jit.GetRegister("DI"));
    }
}
