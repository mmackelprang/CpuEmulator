using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>Piece #2 — the 68000 functional reset (no TomHarte reset vector exists, so this is the
/// reset-state gate). Reset loads the initial SSP from the long at $000000 and the initial PC from the
/// long at $000004 (read big-endian through the bus), and sets SR to supervisor (S=1), interrupt mask 7,
/// trace off (SR=0x2700). Not cycle-gated — functionally-correct landed state is the bar.</summary>
public class M68000ResetTests
{
    // A 24-bit (16 MB) program space with the low 64 KiB mapped, so the $0/$4 vectors land in memory.
    private static M68000Cpu NewCpuWithVectors(uint ssp, uint pc)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 24);
        bus.MapMemory(0, new byte[0x10000], writable: true);
        // The reset vectors are LONGS, big-endian (high byte first), at $0 (SSP) and $4 (PC).
        bus.Write16(0x0000, (ushort)(ssp >> 16));
        bus.Write16(0x0002, (ushort)ssp);
        bus.Write16(0x0004, (ushort)(pc >> 16));
        bus.Write16(0x0006, (ushort)pc);
        return new M68000Cpu(bus);
    }

    [Fact]
    public void Reset_loads_SSP_from_the_long_at_0()
    {
        var cpu = NewCpuWithVectors(ssp: 0x0000_8000, pc: 0x0000_0400);
        cpu.Reset();
        Assert.Equal(0x0000_8000u, cpu.SSP);
    }

    [Fact]
    public void Reset_loads_PC_from_the_long_at_4()
    {
        var cpu = NewCpuWithVectors(ssp: 0x0000_8000, pc: 0x0000_0400);
        cpu.Reset();
        Assert.Equal(0x0000_0400u, cpu.PC);
    }

    [Fact]
    public void Reset_enters_supervisor_with_mask_7_and_trace_off()
    {
        var cpu = NewCpuWithVectors(ssp: 0x0000_8000, pc: 0x0000_0400);
        cpu.Reset();
        Assert.Equal((ushort)0x2700, cpu.SR);   // S(13)=1, mask(10-8)=7, trace(15)=0, CCR=0
        Assert.True(cpu.SupervisorMode);          // the S-bit banking view agrees
    }

    [Fact]
    public void Reset_A7_aliases_the_loaded_SSP_in_supervisor_mode()
    {
        // After reset (supervisor), A7 is the SSP bank, so it reads the loaded supervisor stack pointer.
        var cpu = NewCpuWithVectors(ssp: 0x0000_8000, pc: 0x0000_0400);
        cpu.Reset();
        Assert.Equal(0x0000_8000u, cpu.A7);
    }

    [Fact]
    public void Reset_reads_full_32bit_vectors_big_endian()
    {
        // A full 32-bit vector value proves the long read is wide + big-endian (not truncated to 16 bits).
        var cpu = NewCpuWithVectors(ssp: 0x00AB_CDE0, pc: 0x0012_3456);
        cpu.Reset();
        Assert.Equal(0x00AB_CDE0u, cpu.SSP);
        Assert.Equal(0x0012_3456u, cpu.PC);
    }
}
