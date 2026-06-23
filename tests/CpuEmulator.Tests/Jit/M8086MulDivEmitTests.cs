using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ROADMAP #4 Row MD: the 8086 MUL/IMUL/DIV/IDIV emit arm (F6/F7 /4../7) is LIVE (not
/// all-fallback) and DISPATCHES, and the emitted result registers + FLAGS (CF/OF) + the divide-error
/// INT0 frame are BYTE-IDENTICAL to the interpreter oracle (M8086Cpu.Alu.cs AluMul/AluDiv). The
/// headline byte-identity gate is the F6/F7 files in the M8088JitTom sweep; this file pins the
/// non-vacuity (M8086MulDivEmitSelections &gt; 0, FallbackEmitCount == 0) + the densest correctness
/// pockets (CF/OF significance, the IDIV symmetric-range boundary, the divide-by-zero INT0 frame),
/// comparing a JIT run against a fresh interpreter from the SAME initial state. The interpreter is the
/// oracle.</summary>
public class M8086MulDivEmitTests
{
    private static (M8086Cpu Jit, M8086Cpu Interp) RunBoth(
        byte[] code, out int mulDivEmit, out int fallback, params (string Name, ushort Value)[] seeds)
    {
        const ushort cs = 0x1000, ip = 0x0000;
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        for (int i = 0; i < code.Length; i++) jbus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
        var jit = new M8086Cpu(jbus);
        jit.SetRegister("CS", cs); jit.SetRegister("IP", ip);
        foreach (var (n, v) in seeds) jit.SetRegister(n, v);
        var jc = new JittedCpu<M8086Cpu>(jit, M8086Cpu.JitTarget, jbus);
        long budget = 1; jc.Run(ref budget);
        mulDivEmit = jc.M8086MulDivEmitSelections;
        fallback = jc.FallbackEmitCount;

        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        for (int i = 0; i < code.Length; i++) ibus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip);
        foreach (var (n, v) in seeds) interp.SetRegister(n, v);
        interp.Step();
        return (jit, interp);
    }

    [Fact]
    public void Mul_r_m8_emits_and_is_not_a_fallback()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F6 /4 MUL BL  (modrm E3 = mod=11 reg=4 rm=3 → BL).  AL=0x12, BL=0x10 → AX=0x0120, CF/OF=0.
        var (jit, interp) = RunBoth([0xF6, 0xE3], out int emit, out int fb,
            ("AX", 0x0012), ("BX", 0x0010));
        Assert.True(emit > 0, "MUL was not emitted (the arm never dispatched).");
        Assert.Equal(0, fb);   // no inner.Step callout — the op genuinely emits
        Assert.Equal(interp.GetRegister("AX"), jit.GetRegister("AX"));
        Assert.Equal(interp.GetRegister("FLAGS"), jit.GetRegister("FLAGS"));
    }

    [Fact]
    public void Imul_word_sets_cf_of_when_upper_significant()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F7 /5 IMUL BX (modrm EB = mod=11 reg=5 rm=3 → BX).  AX=0x0100, BX=0x0100 → DX:AX = 0x0001_0000.
        // signed product 0x10000: DX=0x0001 != sign-ext(AX=0) ⇒ CF=OF=1.
        var (jit, interp) = RunBoth([0xF7, 0xEB], out int emit, out _,
            ("AX", 0x0100), ("BX", 0x0100));
        Assert.True(emit > 0);
        Assert.Equal(interp.GetRegister("AX"), jit.GetRegister("AX"));
        Assert.Equal(interp.GetRegister("DX"), jit.GetRegister("DX"));
        Assert.Equal(interp.GetRegister("FLAGS"), jit.GetRegister("FLAGS"));
    }

    [Fact]
    public void Mul_word_clears_cf_of_when_upper_zero()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F7 /4 MUL BX (modrm E3 = mod=11 reg=4 rm=3 → BX). AX=0x0003, BX=0x0004 → DX:AX=0x0000_000C, CF=OF=0.
        var (jit, interp) = RunBoth([0xF7, 0xE3], out int emit, out int fb,
            ("AX", 0x0003), ("BX", 0x0004));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        Assert.Equal(interp.GetRegister("AX"), jit.GetRegister("AX"));
        Assert.Equal(interp.GetRegister("DX"), jit.GetRegister("DX"));
        Assert.Equal(interp.GetRegister("FLAGS"), jit.GetRegister("FLAGS"));
    }

    [Fact]
    public void Imul_byte_negative_product_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F6 /5 IMUL BL (modrm EB = mod=11 reg=5 rm=3 → BL). AL=0xFF (-1), BL=0x02 → AX=0xFFFE (-2).
        // AH=0xFF == sign-ext(AL=0xFE? no — AL after store is 0xFE; sign bit set ⇒ 0xFF) ⇒ CF=OF=0.
        var (jit, interp) = RunBoth([0xF6, 0xEB], out int emit, out _,
            ("AX", 0x00FF), ("BX", 0x0002));
        Assert.True(emit > 0);
        Assert.Equal(interp.GetRegister("AX"), jit.GetRegister("AX"));
        Assert.Equal(interp.GetRegister("FLAGS"), jit.GetRegister("FLAGS"));
    }

    // ─────────────────────────── DIV / IDIV — valid quotient + the fault frame ───────────────────────────

    [Fact]
    public void Div_byte_valid_quotient_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F6 /6 DIV BL (modrm F3 = mod=11 reg=6 rm=3 → BL). AX=0x0064 (100), BL=0x09 → AL=11 (0x0B), AH=1.
        // A non-faulting DIV block: it emits (FallbackEmitCount==0) and the quotient/remainder are byte-identical.
        var (jit, interp) = RunBoth([0xF6, 0xF3], out int emit, out int fb,
            ("AX", 0x0064), ("BX", 0x0009));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);   // the non-faulting DIV genuinely emits (not an interpreter callout)
        Assert.Equal(interp.GetRegister("AX"), jit.GetRegister("AX"));
        Assert.Equal(interp.GetRegister("IP"), jit.GetRegister("IP"));
    }

    [Fact]
    public void Div_word_valid_quotient_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F7 /6 DIV BX (modrm F3 = mod=11 reg=6 rm=3 → BX). DX:AX = 0x0001_0000 / BX=0x0003 → AX=0x5555, DX=1.
        var (jit, interp) = RunBoth([0xF7, 0xF3], out int emit, out int fb,
            ("DX", 0x0001), ("AX", 0x0000), ("BX", 0x0003));
        Assert.True(emit > 0);
        Assert.Equal(0, fb);
        Assert.Equal(interp.GetRegister("AX"), jit.GetRegister("AX"));
        Assert.Equal(interp.GetRegister("DX"), jit.GetRegister("DX"));
        Assert.Equal(interp.GetRegister("IP"), jit.GetRegister("IP"));
    }

    [Fact]
    public void Idiv_byte_symmetric_range_overflow_vectors()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F6 /7 IDIV BL with a quotient of -128 → the 8086 byte-IDIV |quot|>127 quirk (Alu.cs:439) ⇒ INT0.
        // AX=0xFF80 (-128) / BL=0x01 → quot=-128 → OVERFLOW. Seed SS:SP + IVT[0]; compare the WHOLE frame.
        var (jit, jbus, interp, ibus) = RunBothDivFault([0xF6, 0xFB],
            ("AX", 0xFF80), ("BX", 0x0001));
        AssertVectoredFrameIdentical(jit, jbus, interp, ibus);
    }

    [Fact]
    public void Div_by_zero_raises_int0_frame_byte_identical()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // F6 /6 DIV BL with BL=0 → divide-by-zero → INT0. Seed SS:SP + the IVT[0] (CS:IP at [0:0]).
        // Compare the WHOLE machine: CS, IP, SS, SP, FLAGS, AND the three pushed stack words (FLAGS, CS, IP).
        var (jit, jbus, interp, ibus) = RunBothDivFault([0xF6, 0xF3],
            ("AX", 0x0064), ("BX", 0x0000));
        AssertVectoredFrameIdentical(jit, jbus, interp, ibus);
    }

    // ─────────────────────────── divide-error harness: seed SS:SP + the IVT[0] vector ───────────────────────────

    /// <summary>Drive a faulting DIV/IDIV block through both the JIT and a fresh interpreter from the SAME seed.
    /// Seeds SS=0x2000/SP=0x0100 + the IVT[0] (the divide-error vector) at [0:0]=newIP=0x0400, [0:2]=newCS=0x0000
    /// + a deterministic FLAGS so the pushed flags word is stable. Returns both machines (and writes the stack RAM
    /// into each bus) so the caller can memcmp the pushed frame. The interpreter is the oracle.</summary>
    private static (M8086Cpu Jit, AddressSpace JBus, M8086Cpu Interp, AddressSpace IBus) RunBothDivFault(
        byte[] code, params (string Name, ushort Value)[] regSeeds)
    {
        const ushort cs = 0x1000, ip = 0x0000, ss = 0x2000, sp = 0x0100;
        const ushort ivtIp = 0x0400, ivtCs = 0x0000, seedFlags = 0x0202;
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);

        static void Seed(AddressSpace bus, byte[] code, uint phys)
        {
            for (int i = 0; i < code.Length; i++) bus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
            // IVT[0] at physical [0:0] = newIP, [0:2] = newCS (segment-0, little-endian).
            bus.Write8(0, (byte)(ivtIp & 0xFF)); bus.Write8(1, (byte)(ivtIp >> 8));
            bus.Write8(2, (byte)(ivtCs & 0xFF)); bus.Write8(3, (byte)(ivtCs >> 8));
        }

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        Seed(jbus, code, phys);
        var jit = new M8086Cpu(jbus);
        jit.SetRegister("CS", cs); jit.SetRegister("IP", ip);
        jit.SetRegister("SS", ss); jit.SetRegister("SP", sp); jit.SetRegister("FLAGS", seedFlags);
        foreach (var (n, v) in regSeeds) jit.SetRegister(n, v);
        var jc = new JittedCpu<M8086Cpu>(jit, M8086Cpu.JitTarget, jbus);
        long budget = 1; jc.Run(ref budget);

        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        Seed(ibus, code, phys);
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip);
        interp.SetRegister("SS", ss); interp.SetRegister("SP", sp); interp.SetRegister("FLAGS", seedFlags);
        foreach (var (n, v) in regSeeds) interp.SetRegister(n, v);
        interp.Step();
        return (jit, jbus, interp, ibus);
    }

    /// <summary>Assert the JIT and interpreter agree on the WHOLE vectored frame: CS/IP/SS/SP/FLAGS registers AND
    /// the six pushed stack bytes (FLAGS, CS, IP words) read back from each CPU's bus at SS:SP.</summary>
    private static void AssertVectoredFrameIdentical(M8086Cpu jit, AddressSpace jbus, M8086Cpu interp, AddressSpace ibus)
    {
        Assert.Equal(interp.GetRegister("CS"), jit.GetRegister("CS"));
        Assert.Equal(interp.GetRegister("IP"), jit.GetRegister("IP"));
        Assert.Equal(interp.GetRegister("SS"), jit.GetRegister("SS"));
        Assert.Equal(interp.GetRegister("SP"), jit.GetRegister("SP"));
        Assert.Equal(interp.GetRegister("FLAGS"), jit.GetRegister("FLAGS"));
        // The three pushed words live at SS:SP .. SS:SP+5 (IP lowest, then CS, then FLAGS highest).
        uint jbase = (uint)(((jit.GetRegister("SS") << 4) + jit.GetRegister("SP")) & 0xFFFFF);
        uint ibase = (uint)(((interp.GetRegister("SS") << 4) + interp.GetRegister("SP")) & 0xFFFFF);
        for (uint k = 0; k < 6; k++)
            Assert.Equal(ibus.Read8((ibase + k) & 0xFFFFF), jbus.Read8((jbase + k) & 0xFFFFF));
    }
}
