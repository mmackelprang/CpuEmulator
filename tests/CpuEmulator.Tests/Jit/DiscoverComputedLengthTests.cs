using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 4 (Ground truth B + F.3): pins that BlockCompiler.Discover advances by the walk's
/// COMPUTED length (r.Length), NOT a static descriptor field — the J3 generalization. For the 6502
/// the walk returns the per-mode constant, so this is byte-identical; the COMPUTED-length-VARIES
/// proof is the synthetic CPU (Task 7). This file pins the 6502 half over the real table: the run
/// tuple carries the computed length, the discovery cursor advances by it, and PagesSpanned reads
/// it (authorized row 4 — the only other former d.Length reader).</summary>
public class DiscoverComputedLengthTests
{
    private static AddressSpace NewRamSpace()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    private static void Poke(AddressSpace space, ushort at, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            space.Write8((uint)(at + i), bytes[i]);
    }

    private static BlockCompiler NewCompiler(AddressSpace space)
    {
        var opts = new JitOptions();
        return new BlockCompiler(new Mos6502Cpu(space), space, new Fastmem(space, opts), opts);
    }

    [Fact]
    public void Discover_advances_by_the_walk_length_over_mixed_modes()
    {
        // LDA #$01 (2) / NOP (1) / LDA $1234 (3) / BRK (1, fallback ends the block).
        var space = NewRamSpace();
        Poke(space, 0x0200, 0xA9, 0x01, 0xEA, 0xAD, 0x34, 0x12, 0x00);
        var run = NewCompiler(space).Discover(0x0200);

        Assert.Equal(4, run.Count);
        // The PCs are the running sum of the COMPUTED lengths, not a static field read.
        Assert.Equal(0x0200, run[0].Pc);
        Assert.Equal(0x0202, run[1].Pc);   // +2 (LDA #imm)
        Assert.Equal(0x0203, run[2].Pc);   // +1 (NOP)
        Assert.Equal(0x0206, run[3].Pc);   // +3 (LDA abs)
        // Each tuple's computed Length matches the per-instruction byte count.
        Assert.Equal(2, run[0].Length);
        Assert.Equal(1, run[1].Length);
        Assert.Equal(3, run[2].Length);
        Assert.Equal(1, run[3].Length);    // BRK
    }

    [Fact]
    public void Discover_run_tuple_carries_the_computed_length()
    {
        // The run is List<(ushort Pc, OpcodeDescriptor D, int Length)>; each tuple's Length equals
        // DescriptorFor(key).FixedLength for the 6502 (the Fixed degenerate equality).
        var space = NewRamSpace();
        Poke(space, 0x0200, 0xA9, 0x01, 0xAD, 0x34, 0x12, 0x00); // LDA #1 / LDA $1234 / BRK
        var run = NewCompiler(space).Discover(0x0200);

        foreach (var (_, d, length) in run)
            Assert.Equal(Mos6502Cpu.DescriptorFor(d.Opcode).FixedLength, length);
    }

    [Fact]
    public void PagesSpanned_uses_the_computed_length()
    {
        // A 3-byte instruction starting at $02FF straddles into page $03: bytes occupy
        // $02FF, $0300, $0301. PagesSpanned (computed from the tuple Length, not d.Length) must
        // report BOTH page $02 and page $03. End the block with a self JMP at $0302.
        var space = NewRamSpace();
        Poke(space, 0x02FF, 0xAD, 0x34, 0x12);          // LDA $1234 — spans $02FF..$0301
        Poke(space, 0x0302, 0x4C, 0x02, 0x03);          // JMP $0302 (self, ends the block)
        var block = NewCompiler(space).Compile(0x02FF);

        Assert.Contains(0x02, block.SpannedPages);
        Assert.Contains(0x03, block.SpannedPages);
    }
}
