using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0019 FF-1: the per-CPU <see cref="CpuEmulator.Core.Jit.IJitTarget.ProjectBlockKey"/>
/// projection seam. (a) the flat-PC CPUs project the identity (uint)PC — the SAFE premise that makes
/// the ushort->uint widening byte-identical; (b) the 8086 folds the segmented origin
/// ((CS&lt;&lt;4)+IP)&amp;0xFFFFF — the linear physical entry; (c) two (CS,IP) pairs at the SAME physical
/// fold to the SAME key (the overlapping-segment coherence check, Decision 4 gate 3); (d) the same IP
/// offset under a DIFFERENT segment folds to a DIFFERENT key (the aliasing precondition the FF-2 far
/// arms rely on). The CPUs require a bus at construction (no parameterless ctor), so each test wires a
/// minimal mapped bus — the projection reads only register state via ICpuCore.GetRegister.</summary>
public class ProjectBlockKeyTests
{
    private static AddressSpace Ram(int addressBits)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits);
        bus.MapMemory(0x0000, new byte[1 << addressBits], writable: true);
        return bus;
    }

    // The flat-PC CPUs project the identity (uint)PC — the ADR 0019 Decision 2 SAFE premise.
    [Theory]
    [InlineData(0x0000u)]
    [InlineData(0x0001u)]
    [InlineData(0x00FFu)]
    [InlineData(0x0100u)]
    [InlineData(0x8000u)]
    [InlineData(0xFFFFu)]
    public void Mos6502_projects_the_identity_over_PC(uint pc)
    {
        var cpu = new Mos6502Cpu(Ram(16));
        cpu.SetRegister("PC", pc);
        Assert.Equal(pc, Mos6502Cpu.JitTarget.ProjectBlockKey(cpu));
    }

    [Theory]
    [InlineData(0x0000u)]
    [InlineData(0x0100u)]
    [InlineData(0xFFFFu)]
    public void Z80_projects_the_identity_over_PC(uint pc)
    {
        var cpu = new Z80Cpu(Ram(16));
        cpu.SetRegister("PC", pc);
        Assert.Equal(pc, Z80Cpu.JitTarget.ProjectBlockKey(cpu));
    }

    [Theory]
    [InlineData(0x0000_0000u)]
    [InlineData(0x0000_1000u)]
    [InlineData(0x00FF_FFFEu)]
    public void M68000_projects_the_identity_over_PC(uint pc)
    {
        var cpu = new M68000Cpu(new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian));
        cpu.SetRegister("PC", pc);
        Assert.Equal(pc, M68000Cpu.JitTarget.ProjectBlockKey(cpu));
    }

    // The 8086 folds the segmented origin ((CS<<4)+IP)&0xFFFFF — the linear physical entry.
    [Theory]
    [InlineData(0x1000, 0x0100, 0x10100u)]
    [InlineData(0x2000, 0x0100, 0x20100u)]
    [InlineData(0x0000, 0x0000, 0x00000u)]
    [InlineData(0xFFFF, 0xFFFF, 0x0FFEFu)]   // (0xFFFF<<4 + 0xFFFF) & 0xFFFFF = 0x10FFEF & 0xFFFFF
    public void M8086_folds_the_segmented_origin(int cs, int ip, uint expected)
    {
        var cpu = new M8086Cpu(Ram(20));
        cpu.SetRegister("CS", (ulong)cs);
        cpu.SetRegister("IP", (ulong)ip);
        Assert.Equal(expected, M8086Cpu.JitTarget.ProjectBlockKey(cpu));
    }

    // ADR 0019 Decision 4 gate 3 — two (CS,IP) pairs that fold to the SAME physical byte produce the
    // SAME key (overlapping segments execute the same code; the linear key collapses them — the positive
    // case justifying linear over a composite (CS,IP) struct).
    [Fact]
    public void M8086_overlapping_segments_at_the_same_physical_project_the_same_key()
    {
        var a = new M8086Cpu(Ram(20));
        a.SetRegister("CS", 0x1000);   // (0x1000<<4)+0x0100 = 0x10100
        a.SetRegister("IP", 0x0100);

        var b = new M8086Cpu(Ram(20));
        b.SetRegister("CS", 0x1010);   // (0x1010<<4)+0x0000 = 0x10100 — same physical
        b.SetRegister("IP", 0x0000);

        Assert.Equal(
            M8086Cpu.JitTarget.ProjectBlockKey(a),
            M8086Cpu.JitTarget.ProjectBlockKey(b));
    }

    // The aliasing precondition (the FF-2 gate's positive half lives here at the projection layer): two
    // segments at the SAME IP offset but DIFFERENT physical fold to DIFFERENT keys.
    [Fact]
    public void M8086_same_offset_different_segment_projects_different_keys()
    {
        var a = new M8086Cpu(Ram(20));
        a.SetRegister("CS", 0x1000);
        a.SetRegister("IP", 0x0100);

        var b = new M8086Cpu(Ram(20));
        b.SetRegister("CS", 0x2000);
        b.SetRegister("IP", 0x0100);

        Assert.NotEqual(
            M8086Cpu.JitTarget.ProjectBlockKey(a),
            M8086Cpu.JitTarget.ProjectBlockKey(b));
    }
}
