using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ROADMAP #4 Row II: the 8086 soft-interrupt emit arm (CD INT imm8, CC INT3, CE INTO, CF IRET)
/// is LIVE (not all-fallback) and DISPATCHES, and the emitted result — the pushed IVT frame (FLAGS:CS:IP),
/// the IF/TF clear, the vectored CS:IP, and IRET's reserved-bit forcing — is BYTE-IDENTICAL to the
/// interpreter oracle (M8086Cpu.Interrupt.cs InterruptExecute/RaiseInterrupt). The headline byte-identity
/// gate is the CD/CC/CE/CF files in the M8088JitTom sweep; this file pins the non-vacuity
/// (M8086InterruptEmitSelections &gt; 0, FallbackEmitCount == 0) + the densest correctness pockets
/// (the pushed frame, INTO taken/not-taken, the IRET 0x28CF→0xF8C7 reserved-bit case, and the shared-helper
/// frame-identity cross-check vs Row MD's divide-error). The interpreter is the oracle.</summary>
public class M8086InterruptEmitTests
{
    /// <summary>Drive a code block through both the JIT and a fresh interpreter from the SAME seed. Optionally
    /// seeds an IVT entry at [0:vector*4]=newIp, [0:vector*4+2]=newCs (segment-0, little-endian) in BOTH buses
    /// so a vectoring op (INT n / INT3 / INTO-taken) lands on the same handler in each; optionally seeds words
    /// onto the SS:SP stack (lowest word first) BEFORE the run, for the IRET pop frame. Returns both machines +
    /// both buses so the caller can memcmp the pushed/popped stack frame.</summary>
    private static (M8086Cpu Jit, AddressSpace JBus, M8086Cpu Interp, AddressSpace IBus) RunBoth(
        byte[] code, out int emit, out int fb,
        (byte Vector, ushort NewIp, ushort NewCs)? seedIvt = null,
        ushort[]? seedStackWords = null,
        params (string Name, ushort Value)[] seeds)
    {
        const ushort cs = 0x1000, ip = 0x0000;
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        ushort ss = 0, sp = 0;
        foreach (var (n, v) in seeds) { if (n == "SS") ss = v; if (n == "SP") sp = v; }

        void Seed(AddressSpace bus)
        {
            for (int i = 0; i < code.Length; i++) bus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
            if (seedIvt is { } v)
            {
                uint off = (uint)(v.Vector * 4);
                bus.Write8(off, (byte)(v.NewIp & 0xFF)); bus.Write8(off + 1, (byte)(v.NewIp >> 8));
                bus.Write8(off + 2, (byte)(v.NewCs & 0xFF)); bus.Write8(off + 3, (byte)(v.NewCs >> 8));
            }
            if (seedStackWords is { } words)
            {
                uint sbase = (uint)(((ss << 4) + sp) & 0xFFFFF);
                for (int w = 0; w < words.Length; w++)
                {
                    bus.Write8((sbase + (uint)(w * 2)) & 0xFFFFF, (byte)(words[w] & 0xFF));
                    bus.Write8((sbase + (uint)(w * 2) + 1) & 0xFFFFF, (byte)(words[w] >> 8));
                }
            }
        }

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        Seed(jbus);
        var jit = new M8086Cpu(jbus);
        jit.SetRegister("CS", cs); jit.SetRegister("IP", ip);
        foreach (var (n, val) in seeds) jit.SetRegister(n, val);
        var jc = new JittedCpu<M8086Cpu>(jit, M8086Cpu.JitTarget, jbus);
        long budget = 1; jc.Run(ref budget);
        emit = jc.M8086InterruptEmitSelections;
        fb = jc.FallbackEmitCount;

        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        Seed(ibus);
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip);
        foreach (var (n, val) in seeds) interp.SetRegister(n, val);
        interp.Step();
        return (jit, jbus, interp, ibus);
    }

    /// <summary>Assert the JIT and interpreter agree on the WHOLE post-op state: CS/IP/SS/SP/FLAGS registers AND
    /// the six pushed stack bytes (the IP, CS, FLAGS words) read back from each CPU's bus at SS:SP.</summary>
    private static void AssertVectoredFrameIdentical(M8086Cpu jit, AddressSpace jbus, M8086Cpu interp, AddressSpace ibus)
    {
        Assert.Equal(interp.GetRegister("CS"), jit.GetRegister("CS"));
        Assert.Equal(interp.GetRegister("IP"), jit.GetRegister("IP"));
        Assert.Equal(interp.GetRegister("SS"), jit.GetRegister("SS"));
        Assert.Equal(interp.GetRegister("SP"), jit.GetRegister("SP"));
        Assert.Equal(interp.GetRegister("FLAGS"), jit.GetRegister("FLAGS"));
        uint jbase = (uint)(((jit.GetRegister("SS") << 4) + jit.GetRegister("SP")) & 0xFFFFF);
        uint ibase = (uint)(((interp.GetRegister("SS") << 4) + interp.GetRegister("SP")) & 0xFFFFF);
        for (uint k = 0; k < 6; k++)
            Assert.Equal(ibus.Read8((ibase + k) & 0xFFFFF), jbus.Read8((jbase + k) & 0xFFFFF));
    }

    // ─────────────────────────── INT n / INT3 — the frame + vector + block-end ───────────────────────────

    [Fact]
    public void Int_n_emits_pushes_frame_and_vectors()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // CD 21 INT 0x21. Seed SS:SP, FLAGS, and the IVT[0x21] (CS:IP at [0:0x84]). Compare CS, IP, SS, SP,
        // FLAGS, AND the 6 pushed stack bytes (FLAGS, CS, IP) — byte-identical to a fresh interpreter.
        var (jit, jbus, interp, ibus) = RunBoth([0xCD, 0x21], out int emit, out int fb,
            seedIvt: (0x21, NewIp: 0x1234, NewCs: 0x5678),
            seeds: [("SS", 0x2000), ("SP", 0x0100), ("FLAGS", 0x0202)]);
        Assert.True(emit > 0, "INT n was not emitted (the interrupt arm never dispatched).");
        Assert.Equal(0, fb);
        Assert.Equal(0x5678ul, jit.GetRegister("CS"));   // vectored CS
        Assert.Equal(0x1234ul, jit.GetRegister("IP"));   // vectored IP
        AssertVectoredFrameIdentical(jit, jbus, interp, ibus);
    }

    [Fact]
    public void Int3_vectors_through_vector_3()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // CC INT3 → vector 3 (CS:IP at [0:0x0C]). One-byte op; the pushed IP is the return point (pc+1).
        var (jit, jbus, interp, ibus) = RunBoth([0xCC], out int emit, out int fb,
            seedIvt: (3, NewIp: 0x4321, NewCs: 0x8765),
            seeds: [("SS", 0x2000), ("SP", 0x0100), ("FLAGS", 0x0202)]);
        Assert.True(emit > 0, "INT3 was not emitted (the interrupt arm never dispatched).");
        Assert.Equal(0, fb);
        Assert.Equal(0x8765ul, jit.GetRegister("CS"));
        Assert.Equal(0x4321ul, jit.GetRegister("IP"));
        AssertVectoredFrameIdentical(jit, jbus, interp, ibus);
    }

    // ─────────────────────────── INTO — conditional on OF ───────────────────────────

    [Fact]
    public void Into_with_of_set_vectors()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // CE INTO with OF set → vector 4 (CS:IP at [0:0x10]). FLAGS 0x0802 = OF(bit11)|bit1. Frame pushed.
        var (jit, jbus, interp, ibus) = RunBoth([0xCE], out int emit, out int fb,
            seedIvt: (4, NewIp: 0x0BAD, NewCs: 0x0F00),
            seeds: [("SS", 0x2000), ("SP", 0x0100), ("FLAGS", 0x0802)]);
        Assert.True(emit > 0, "INTO was not emitted (the interrupt arm never dispatched).");
        Assert.Equal(0, fb);
        Assert.Equal(0x0F00ul, jit.GetRegister("CS"));
        Assert.Equal(0x0BADul, jit.GetRegister("IP"));
        AssertVectoredFrameIdentical(jit, jbus, interp, ibus);
    }

    [Fact]
    public void Into_without_of_is_noop()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // CE INTO with OF clear → no push, no vector. IP advances past the 1-byte op; block ends. Registers ==
        // interpreter (SP unchanged, CS unchanged, IP at the return point, FLAGS unchanged).
        var (jit, jbus, interp, ibus) = RunBoth([0xCE], out int emit, out int fb,
            seedIvt: (4, NewIp: 0x0BAD, NewCs: 0x0F00),
            seeds: [("SS", 0x2000), ("SP", 0x0100), ("FLAGS", 0x0202)]);   // OF clear
        Assert.True(emit > 0, "INTO was not emitted (the interrupt arm never dispatched).");
        Assert.Equal(0, fb);
        AssertVectoredFrameIdentical(jit, jbus, interp, ibus);
        Assert.Equal(0x1000ul, jit.GetRegister("CS"));   // CS unchanged
        Assert.Equal(0x0001ul, jit.GetRegister("IP"));   // IP at the return point (pc+1)
        Assert.Equal(0x0100ul, jit.GetRegister("SP"));   // SP unchanged (no push)
    }

    // ─────────────────────────── IRET — pop IP:CS:FLAGS + reserved-bit forcing ───────────────────────────

    [Fact]
    public void Iret_pops_ip_cs_flags_and_forces_reserved_bits()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // CF IRET. Seed a far+flags frame at SS:SP (lowest word = IP, then CS, then FLAGS): IP=0x1234,
        // CS=0x5678, FLAGS=0x28CF (the corpus case that forces to 0xF8C7 — IRET's flags-mask is 0xFFFF, so
        // the reserved-bit forcing (popped & 0x0FD5)|0xF002 is load-bearing). Assert CS/IP/SP/FLAGS identical.
        var (jit, jbus, interp, ibus) = RunBoth([0xCF], out int emit, out int fb,
            seedStackWords: [0x1234, 0x5678, 0x28CF],
            seeds: [("SS", 0x2000), ("SP", 0x0100)]);
        Assert.True(emit > 0, "IRET was not emitted (the interrupt arm never dispatched).");
        Assert.Equal(0, fb);
        Assert.Equal(0x5678ul, jit.GetRegister("CS"));
        Assert.Equal(0x1234ul, jit.GetRegister("IP"));
        Assert.Equal((ulong)((0x28CF & 0x0FD5) | 0xF002), jit.GetRegister("FLAGS"));   // == 0xF8C7
        Assert.Equal(0xF8C7ul, jit.GetRegister("FLAGS"));
        Assert.Equal(interp.GetRegister("CS"), jit.GetRegister("CS"));
        Assert.Equal(interp.GetRegister("IP"), jit.GetRegister("IP"));
        Assert.Equal(interp.GetRegister("SP"), jit.GetRegister("SP"));   // SP += 6
        Assert.Equal(interp.GetRegister("FLAGS"), jit.GetRegister("FLAGS"));
    }

    // ─────────────────────────── the shared-helper cross-check (Task 6) ───────────────────────────

    [Fact]
    public void Int0_and_divzero_push_byte_identical_frames()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // DECISION MD-2: an INT 0 block (Row II) and a DIV-by-zero block (Row MD) BOTH vector through [0:0] via
        // the SHARED EmitM8086RaiseInterrupt. With the SAME seed (SS:SP, FLAGS, IVT[0]) the pushed frame —
        // FLAGS:CS:IP, the vector, the IF/TF clear — must be byte-identical, EXCEPT the pushed IP (the divide
        // error's return IP is past F6 /6 = pc+2; INT 0's is past CD 00 = pc+2 too). So both push the SAME IP
        // here — choose F6 F0 (DIV with /6? no): use CD 00 vs F6 F3 (both 2-byte → return IP 0x0002), identical.
        // Seed FLAGS with IF+TF set so the clear is observable in the pushed-and-then-cleared FLAGS register.
        var (jit1, jb1, _, _) = RunBoth([0xCD, 0x00], out int e1, out int f1,
            seedIvt: (0, NewIp: 0x0400, NewCs: 0x0000),
            seeds: [("SS", 0x2000), ("SP", 0x0100), ("FLAGS", 0x0302)]);   // 0x0302: IF(bit9)|TF(bit8)|bit1
        Assert.True(e1 > 0); Assert.Equal(0, f1);

        // The DIV-by-zero block (Row MD): F6 /6 DIV BL with BL=0 → INT0 through the SAME helper.
        var (jit2, jb2, _, _) = RunBoth([0xF6, 0xF3], out _, out _,
            seedIvt: (0, NewIp: 0x0400, NewCs: 0x0000),
            seeds: [("SS", 0x2000), ("SP", 0x0100), ("FLAGS", 0x0302), ("AX", 0x0064), ("BX", 0x0000)]);

        // Same vectored CS:IP, same SP, same cleared FLAGS register.
        Assert.Equal(jit2.GetRegister("CS"), jit1.GetRegister("CS"));
        Assert.Equal(jit2.GetRegister("IP"), jit1.GetRegister("IP"));
        Assert.Equal(jit2.GetRegister("SP"), jit1.GetRegister("SP"));
        Assert.Equal(jit2.GetRegister("FLAGS"), jit1.GetRegister("FLAGS"));   // both cleared IF+TF identically
        // Byte-identical pushed frame (FLAGS:CS:IP at SS:SP) — both went through EmitM8086RaiseInterrupt.
        uint b1 = (uint)(((jit1.GetRegister("SS") << 4) + jit1.GetRegister("SP")) & 0xFFFFF);
        uint b2 = (uint)(((jit2.GetRegister("SS") << 4) + jit2.GetRegister("SP")) & 0xFFFFF);
        for (uint k = 0; k < 6; k++)
            Assert.Equal(jb2.Read8((b2 + k) & 0xFFFFF), jb1.Read8((b1 + k) & 0xFFFFF));
    }
}
