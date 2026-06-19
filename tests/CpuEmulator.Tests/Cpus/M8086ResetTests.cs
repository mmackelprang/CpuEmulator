using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>Piece #2 — the 8086 functional reset. Reset jams CS=0xFFFF, IP=0x0000 (physical entry
/// 0xFFFF0 = (CS&lt;&lt;4)+IP), DS=ES=SS=0x0000, and clears FLAGS. No bus read — a pure register jam. No
/// TomHarte reset vector exists, so this is the landed-state gate (not cycle-gated).</summary>
public class M8086ResetTests
{
    private static M8086Cpu NewDirtyCpu()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        var cpu = new M8086Cpu(bus);
        // Dirty every reset-affected register so the test proves Reset() sets them, not their defaults.
        cpu.CS = 0x1234; cpu.IP = 0x5678;
        cpu.DS = 0x1111; cpu.ES = 0x2222; cpu.SS = 0x3333;
        cpu.FLAGS = 0xFFFF;
        return cpu;
    }

    [Fact]
    public void Reset_sets_CS_to_FFFF_and_IP_to_0000()
    {
        var cpu = NewDirtyCpu();
        cpu.Reset();
        Assert.Equal((ushort)0xFFFF, cpu.CS);
        Assert.Equal((ushort)0x0000, cpu.IP);
    }

    [Fact]
    public void Reset_clears_the_data_extra_and_stack_segments()
    {
        var cpu = NewDirtyCpu();
        cpu.Reset();
        Assert.Equal((ushort)0x0000, cpu.DS);
        Assert.Equal((ushort)0x0000, cpu.ES);
        Assert.Equal((ushort)0x0000, cpu.SS);
    }

    [Fact]
    public void Reset_clears_FLAGS()
    {
        var cpu = NewDirtyCpu();
        cpu.Reset();
        Assert.Equal((ushort)0x0000, cpu.FLAGS);
    }
}
