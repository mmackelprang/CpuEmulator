using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0019 Decision 4 gate 2 — the far-transfer aliasing regression (the load-bearing, un-fakeable
/// FF-1+FF-2 proof). Two segments at the SAME IP offset (0x0100) hold DIFFERENT code (each writes a segment-unique
/// byte). A far JMP from segment A into segment B must run B's OWN code, not A's. On the OLD ushort-IP key both
/// alias to _blocks[0x0100] and B silently re-runs A's compiled block (the corruption bug); on the FF-1 linear
/// (CS&lt;&lt;4)+IP key they are DISTINCT blocks (0x10100 vs 0x20100). The far JMP (EA, FF-2 Task 4) is what arms the
/// bug — it changes CS mid-chain, so the successor must key under the NEW segment. This test PASSES with FF-1 + the
/// far arms; it FAILS if the dispatcher keys on the bare IP (the red→green proof is documented in the PR body).</summary>
public class M8086AliasingRegressionTests
{
    // Segment A @ CS=0x1000, IP=0x0100 (phys 0x10100): MOV byte [0x0080],0xAA ; far JMP 0x2000:0x0100.
    //   C6 06 80 00 AA   = MOV byte [0x0080], 0xAA   (writes A's marker to DS:0x0080 = phys 0x0080, DS=0)
    //   EA 00 01 00 20   = JMP 0x2000:0x0100         (IP_lo IP_hi CS_lo CS_hi)
    private static readonly byte[] SegA = [0xC6, 0x06, 0x80, 0x00, 0xAA, 0xEA, 0x00, 0x01, 0x00, 0x20];
    // Segment B @ CS=0x2000, IP=0x0100 (phys 0x20100): MOV byte [0x0082],0xBB ; HLT.
    //   C6 06 82 00 BB   = MOV byte [0x0082], 0xBB   (B's marker)
    //   F4               = HLT
    private static readonly byte[] SegB = [0xC6, 0x06, 0x82, 0x00, 0xBB, 0xF4];

    private static (JittedCpu<M8086Cpu> Jit, AddressSpace Bus, M8086Cpu Inner) Build()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        for (uint i = 0; i < SegA.Length; i++) bus.Write8(0x10100 + i, SegA[i]);
        for (uint i = 0; i < SegB.Length; i++) bus.Write8(0x20100 + i, SegB[i]);
        var inner = new M8086Cpu(bus);
        inner.SetRegister("CS", 0x1000); inner.SetRegister("IP", 0x0100);
        inner.SetRegister("DS", 0x0000);   // markers land at absolute 0x0080 / 0x0082
        inner.SetRegister("SS", 0x0000); inner.SetRegister("SP", 0x1000);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, bus);
        return (jit, bus, inner);
    }

    /// <summary>The behavioral proof: A's marker AND B's marker must BOTH be present after the far JMP — proving
    /// segment B ran ITS OWN code (the C6 to 0x0082), not a re-run of A's block (which would write 0x0080 again /
    /// never touch 0x0082). On the bare-IP key B aliases A's block and 0x0082 stays 0x00.</summary>
    [Fact]
    public void Far_jmp_between_segments_at_the_same_offset_runs_each_segments_own_code()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (jit, bus, _) = Build();
        long budget = 64; jit.Run(ref budget);   // run A → far JMP → B → HLT

        Assert.Equal(0xAA, bus.Read8(0x0080));   // segment A executed
        Assert.Equal(0xBB, bus.Read8(0x0082));   // segment B executed ITS OWN code (the aliasing fix)
    }

    /// <summary>The key-level proof: the cache holds TWO distinct linear keys (0x10100 and 0x20100), NOT one bare-IP
    /// key (0x0100). The bare-IP key being ABSENT is the inverse witness — the dispatcher never keyed on the
    /// 16-bit IP alone (the old bug).</summary>
    [Fact]
    public void Two_segments_same_offset_compile_to_distinct_blocks()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (jit, _, _) = Build();
        long budget = 64; jit.Run(ref budget);

        Assert.True(jit.CacheContainsBlockKey(0x10100u));   // segment A's block
        Assert.True(jit.CacheContainsBlockKey(0x20100u));   // segment B's block — DISTINCT (the fix)
        Assert.False(jit.CacheContainsBlockKey(0x00100u));  // NOT keyed on the bare IP (the old bug)
    }
}
