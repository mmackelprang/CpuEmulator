using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000MoveSystemTests
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
    public void Movea_w_sign_extends_to_32_and_sets_no_ccr()
    {
        // MOVEA.w D0,A1 = 0x3240 (size .w via Move enc=11 at 13-12; dest mode 001=An, dest reg 001=A1; src Dn).
        var (cpu, _) = Build((0x1000, 0x32), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000F000);   // .w source 0xF000 → sign-extends to 0xFFFFF000
        cpu.SetRegister("SR", 0x0000);
        cpu.Step();
        Assert.Equal(0xFFFFF000u, (uint)cpu.GetRegister("A1"));   // sign-extended whole-An write
        Assert.Equal(0x0000u, (uint)cpu.GetRegister("SR") & 0xFF); // MOVEA sets NO CCR
    }

    [Fact]
    public void Move_to_ccr_loads_the_low_byte()
    {
        // MOVE to CCR D0 = 0x44C0 (opIndex 37; source = Dn 0). D0 low byte → CCR (only bits 0-4 settable).
        var (cpu, _) = Build((0x1000, 0x44), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x0000001F);   // all 5 CCR bits
        cpu.SetRegister("SR", 0x2700);        // supervisor, mask 7 (CCR currently 0)
        cpu.Step();
        Assert.Equal(0x1Fu, (uint)cpu.GetRegister("SR") & 0x1F);   // CCR bits loaded
    }

    [Fact]
    public void Move_to_sr_is_privileged_and_loads_the_full_word()
    {
        // MOVE to SR D0 = 0x46C0 (opIndex 38; source Dn 0). In supervisor mode, SR <- D0.w.
        var (cpu, _) = Build((0x1000, 0x46), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2000);        // supervisor (S=bit13) set
        cpu.SetRegister("D0", 0x00002700);    // new SR value
        cpu.Step();
        Assert.Equal(0x2700u, (uint)cpu.GetRegister("SR"));
    }

    [Fact]
    public void Move_from_sr_stores_the_status_word()
    {
        // MOVE from SR D0 = 0x40C0 (opIndex 36; dest = Dn 0). SR.w -> D0 low word (.w, partial).
        var (cpu, _) = Build((0x1000, 0x40), (0x1001, 0xC0));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2715);
        cpu.SetRegister("D0", 0xFFFF0000);
        cpu.Step();
        Assert.Equal(0xFFFF2715u, (uint)cpu.GetRegister("D0"));   // .w partial write of SR into D0
    }

    [Fact]
    public void Move_usp_to_an_and_back()
    {
        // MOVE USP -> A1 (MOVEfromUSP) = 0x4E69 (opIndex 35; bit 3 = 1 = from USP; reg = A1). PRIVILEGED.
        var (cpu, _) = Build((0x1000, 0x4E), (0x1001, 0x69));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("SR", 0x2000);          // supervisor
        cpu.SetRegister("USP", 0x00ABCDEF);
        cpu.Step();
        Assert.Equal(0x00ABCDEFu, (uint)cpu.GetRegister("A1"));
    }
}
