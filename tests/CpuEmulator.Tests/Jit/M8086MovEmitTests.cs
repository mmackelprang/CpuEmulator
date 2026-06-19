using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M6 PR-B: the 8086 MOV emit arm is LIVE (not all-fallback) and DISPATCHES — the non-vacuous gate
/// companion to the M8088 JIT TomHarte sweep. A MOV block emits 0 fallbacks + a positive MOV-selection count,
/// and a CS≠0 MOV (the segmentation case the corpus exercises) lands the byte at the (CS&lt;&lt;4)+IP-resolved EA
/// (proving Task 0 — the segmented Discover + emit-time physical origin).</summary>
public class M8086MovEmitTests
{
    private static (BlockCompiler<M8086Cpu> C, M8086Cpu Cpu, AddressSpace Bus) Make(ushort cs, ushort ip, params byte[] code)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) bus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
        var cpu = new M8086Cpu(bus);
        cpu.SetRegister("CS", cs); cpu.SetRegister("IP", ip);
        var opts = new JitOptions();
        return (new BlockCompiler<M8086Cpu>(cpu, M8086Cpu.JitTarget, bus, new Fastmem(bus, opts), opts), cpu, bus);
    }

    /// <summary>5a/5b: a pure-register MOV block emits real IL (FallbackEmitCount == 0 for the MOV) AND the arm
    /// actually DISPATCHED (M8086MovEmitSelections &gt; 0) — the un-fakeable non-vacuity proof.</summary>
    [Fact]
    public void Mov_block_emits_no_fallback_and_dispatches_the_arm()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;   // emit-only proof
        // B8 34 12  MOV AX, 0x1234  — at CS=0x1000, IP=0x0020 (physical 0x10020). A pure-register MOV.
        var (c, _, _) = Make(0x1000, 0x0020, 0xB8, 0x34, 0x12, 0x90);   // trailing NOP ends discovery cleanly
        _ = c.Compile(0x0020);
        Assert.Equal(1, c.FallbackEmitCount);             // ONLY the trailing NOP fell back; the MOV emitted real IL
        Assert.True(c.M8086MovEmitSelections > 0,         // ... and the arm actually dispatched (non-vacuous)
            "EmitM8086Mov was never selected — the MOV gate-flip / dispatch route is not wired.");
    }

    /// <summary>The NEGATIVE control — a block of only a fallback op (NOP) selects the MOV arm zero times, so the
    /// positive case above is meaningful (the counter is not always-tripping).</summary>
    [Fact]
    public void Non_MOV_block_selects_the_MOV_arm_zero_times()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (c, _, _) = Make(0x1000, 0x0020, 0x90);   // NOP only — falls back, no MOV row
        _ = c.Compile(0x0020);
        Assert.Equal(0, c.M8086MovEmitSelections);
    }

    /// <summary>5b: a CS≠0 memory MOV lands the byte at the SEGMENTED data EA (proving Task 0). Run through the
    /// real emit path (JittedCpu&lt;M8086Cpu&gt;) for one instruction. C6 06 00 02 AB = MOV byte [0x0200], 0xAB
    /// (mod=00 rm=110 disp16-direct, DS default). CS=0x2000 ⇒ code physical 0x20000; DS=0x3000 ⇒ data physical
    /// (0x3000&lt;&lt;4)+0x0200 = 0x30200 — NOT 0x0200 (which a CS=0 / flat-IP Discover would have mis-resolved).</summary>
    [Fact]
    public void Mov_to_memory_at_nonzero_cs_resolves_the_segmented_ea()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var (_, cpu, bus) = Make(0x2000, 0x0000, 0xC6, 0x06, 0x00, 0x02, 0xAB, 0x90);
        cpu.SetRegister("DS", 0x3000);
        var jit = new JittedCpu<M8086Cpu>(cpu, M8086Cpu.JitTarget, bus);
        long budget = 1; jit.Run(ref budget);
        Assert.Equal(0xAB, bus.Read8(0x30200));   // the SEGMENTED data EA (CS=0 / flat IP would mis-resolve to 0x0200)
        Assert.Equal(0x00, bus.Read8(0x00200));   // ... and the flat (CS=0) address was NOT written
    }

    /// <summary>5b: the WORD memory MOV with the segment-relative offset wrap — MOV word [0xFFFF], 0xBEEF at
    /// DS=0x4000. Low byte at offset 0xFFFF (physical (0x4000&lt;&lt;4)+0xFFFF = 0x4FFFF); HIGH byte's offset wraps to
    /// 0x0000 WITHIN the segment (physical (0x4000&lt;&lt;4)+0x0000 = 0x40000), NOT 0x50000. This pins the
    /// ReadEaWordWrapped/WriteEaWordWrapped quirk in the emitted IL.</summary>
    [Fact]
    public void Mov_word_to_memory_wraps_the_offset_within_the_segment()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        // C7 06 FF FF EF BE = MOV word [0xFFFF], 0xBEEF (mod=00 rm=110 disp16-direct, DS default).
        var (_, cpu, bus) = Make(0x2000, 0x0000, 0xC7, 0x06, 0xFF, 0xFF, 0xEF, 0xBE, 0x90);
        cpu.SetRegister("DS", 0x4000);
        var jit = new JittedCpu<M8086Cpu>(cpu, M8086Cpu.JitTarget, bus);
        long budget = 1; jit.Run(ref budget);
        Assert.Equal(0xEF, bus.Read8(0x4FFFF));   // low byte at offset 0xFFFF
        Assert.Equal(0xBE, bus.Read8(0x40000));   // high byte at the WRAPPED offset 0x0000 (within the segment)
        Assert.Equal(0x00, bus.Read8(0x50000));   // NOT physical+1 (the physical-increment form would land here)
    }
}
