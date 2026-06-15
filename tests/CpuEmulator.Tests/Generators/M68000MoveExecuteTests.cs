using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000MoveExecuteTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build(params (uint Addr, byte Val)[] mem)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        foreach (var (a, v) in mem) bus.Write8(a, v);
        return (new M68000Cpu(bus), bus);
    }

    [Fact]
    public void Move_b_into_data_register_is_a_partial_write()
    {
        // MOVE.b D0,D1 = 0x1200 (00 size-b(01) dest-reg=001 dest-mode=000 src-mode=000 src-reg=000).
        var (cpu, _) = Build((0x1000, 0x12), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x000000AB);
        cpu.SetRegister("D1", 0x11223344);   // upper 24 bits must survive a .b write
        cpu.Step();
        Assert.Equal(0x112233ABu, (uint)cpu.GetRegister("D1"));   // only the low byte changed
    }

    [Fact]
    public void Move_w_sets_CCR_N_and_Z_clears_V_C()
    {
        // MOVE.w D0,D1 = 0x3200. D0 low word = 0x8000 → N set, Z clear, V=C=0.
        var (cpu, _) = Build((0x1000, 0x32), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x00008000);
        cpu.SetRegister("D1", 0);
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        byte ccr = (byte)((uint)cpu.GetRegister("SR") & 0xFF);
        Assert.Equal(0x08, ccr & 0x08);   // N (bit 3) set
        Assert.Equal(0x00, ccr & 0x04);   // Z (bit 2) clear
        Assert.Equal(0x00, ccr & 0x03);   // V (bit 1) + C (bit 0) clear
    }

    [Fact]
    public void Move_l_memory_to_register_postincrement_writes_back()
    {
        // MOVE.l (A0)+,D1 = 0x2218 (00 size-l(10) dest-reg=001 dest-mode=000 src-mode=011 src-reg=000).
        var (cpu, bus) = Build((0x1000, 0x22), (0x1001, 0x18));
        // source long at 0x2000 = 0xDEADBEEF (big-endian)
        bus.Write8(0x2000, 0xDE); bus.Write8(0x2001, 0xAD); bus.Write8(0x2002, 0xBE); bus.Write8(0x2003, 0xEF);
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.Step();
        Assert.Equal(0xDEADBEEFu, (uint)cpu.GetRegister("D1"));
        Assert.Equal(0x2004u, (uint)cpu.GetRegister("A0"));   // (A0)+ advanced by 4 (.l)
    }
}
