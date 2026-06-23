using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0019 FF-1: the 8086 near-flow static chain edge keys its successor on the LINEAR
/// physical (CS&lt;&lt;4)+IP — the same key the dispatcher's ProjectBlockKey computes — NOT the bare 16-bit
/// IP. Before the Task-7 fold the near arm pushed the bare IP, so under a non-zero CS the successor was
/// keyed at the wrong (un-segmented) address; this pin makes the fold un-fakeable. (The far forms stay
/// fallback — DECISION D-1 — so this is purely a near-edge key correction, not a far emit.)</summary>
public class M8086NearChainKeyTests
{
    [Fact]
    public void Near_jmp_under_a_nonzero_cs_chains_to_the_linear_key()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;

        // A near JMP at CS=0x2000, IP=0x0100 to IP=0x0120: the successor block must be keyed on the
        // LINEAR physical (0x2000<<4)+0x0120 = 0x20120, not the bare IP 0x0120.
        const ushort cs = 0x2000;
        const ushort ip = 0x0100;
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);     // 0x20100
        // EB 1E = JMP rel8 +0x1E: fallThrough = IP+2 = 0x0102, target = 0x0102 + 0x1E = 0x0120.
        bus.Write8(phys + 0, 0xEB); bus.Write8(phys + 1, 0x1E);
        // The successor block at IP 0x0120 (phys 0x20120): EB FE = JMP $-2 (self-loop) — a one-op block
        // that ends at the JMP and parks, so it compiles + the chain edge into it is taken.
        bus.Write8((phys + 0x20), 0xEB); bus.Write8((phys + 0x21), 0xFE);

        var inner = new M8086Cpu(bus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, bus);

        long budget = 16; jit.Run(ref budget);   // run the JMP block, take the near chain edge into 0x20120

        Assert.True(jit.CacheContainsBlockKey(0x20120u),
            "the near chain edge must key the successor on the linear (CS<<4)+IP = 0x20120");
        Assert.False(jit.CacheContainsBlockKey(0x00120u),
            "the successor must NOT be keyed on the bare IP 0x0120");
        Assert.True(jit.ChainStepCount > 0, "the near chain edge was actually taken");
    }
}
