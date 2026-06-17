using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>
/// M5.5b — vector-free synthetic proof of the 8086 BCD/ASCII adjusts (DAA/DAS/AAA/AAS/AAM/AAD). Each test sets
/// a known AL/AH/FLAGS pre-state, Steps the real <see cref="M8086Cpu"/> once over the single-byte (or imm8)
/// opcode, and asserts the AL/AH/defined-flags result. The undefined flags (OF for DAA/DAS; the rest for
/// AAA/AAS) are NOT asserted here — only the defined ones the TomHarte mask keeps. Mirrors
/// <see cref="M8086MovExecuteTests"/>'s construction.
/// </summary>
public class M8086BcdExecuteTests
{
    private static M8086Cpu NewCpu(out AddressSpace bus)
    {
        bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        return new M8086Cpu(bus);
    }

    private static void LoadCode(AddressSpace bus, params byte[] code)
    {
        for (uint i = 0; i < code.Length; i++) bus.Write8(i, code[i]);
    }

    private const ushort CF = 1 << 0, PF = 1 << 2, AF = 1 << 4, ZF = 1 << 6, SF = 1 << 7;
    private static bool Flag(M8086Cpu cpu, ushort bit) => ((ushort)cpu.GetRegister("FLAGS") & bit) != 0;

    [Fact]
    public void Daa_adjusts_low_nibble_over_9()
    {
        // AL = 0x0A (binary 10, low nibble > 9) → DAA → 0x10 (BCD), AF set.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x000A);
        LoadCode(bus, 0x27);   // DAA
        cpu.Step();
        Assert.Equal(0x10u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, AF));
    }

    [Fact]
    public void Daa_adjusts_high_nibble_and_sets_CF()
    {
        // AL = 0x9A: low nibble 0xA > 9 → +6 = 0xA0; old_AL 0x9A > 0x99 → +0x60 = 0x00, CF set, ZF set.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x009A);
        cpu.SetRegister("FLAGS", 0);
        LoadCode(bus, 0x27);
        cpu.Step();
        Assert.Equal(0x00u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
        Assert.True(Flag(cpu, ZF));
    }

    [Fact]
    public void Daa_with_incoming_CF_forces_high_adjust()
    {
        // AL = 0x10, CF=1 going in → high adjust fires (old_CF): AL += 0x60 = 0x70, CF set.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0010);
        cpu.SetRegister("FLAGS", CF);
        LoadCode(bus, 0x27);
        cpu.Step();
        Assert.Equal(0x70u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
    }

    [Fact]
    public void Das_adjusts_low_nibble()
    {
        // AL = 0x0F: low nibble > 9 → -6 = 0x09. No high adjust (old_AL 0x0F < 0x99, CF clear).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x000F);
        cpu.SetRegister("FLAGS", 0);
        LoadCode(bus, 0x2F);   // DAS
        cpu.Step();
        Assert.Equal(0x09u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, AF));
    }

    [Fact]
    public void Das_high_adjust_sets_CF()
    {
        // AL = 0xFF: low nibble 0xF > 9 → -6 = 0xF9; old_AL 0xFF > 0x99 → -0x60 = 0x99, CF set.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x00FF);
        cpu.SetRegister("FLAGS", 0);
        LoadCode(bus, 0x2F);
        cpu.Step();
        Assert.Equal(0x99u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, CF));
    }

    [Fact]
    public void Aaa_adjusts_and_increments_AH()
    {
        // AL = 0x0B (low nibble > 9): AAA → AX += 0x106, AL &= 0xF. AH 0 → 1, AL 0xB → 0x01. AF=CF=1.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x000B);
        LoadCode(bus, 0x37);   // AAA
        cpu.Step();
        Assert.Equal(0x01u, cpu.GetRegister("AL"));
        Assert.Equal(0x01u, cpu.GetRegister("AH"));
        Assert.True(Flag(cpu, AF));
        Assert.True(Flag(cpu, CF));
    }

    [Fact]
    public void Aaa_no_adjust_when_low_nibble_le_9_and_AF_clear()
    {
        // AL = 0x05, AF clear → no adjust; AL &= 0xF = 0x05, AF=CF=0.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0205);   // AH=0x02
        cpu.SetRegister("FLAGS", 0);
        LoadCode(bus, 0x37);
        cpu.Step();
        Assert.Equal(0x05u, cpu.GetRegister("AL"));
        Assert.Equal(0x02u, cpu.GetRegister("AH"));   // unchanged
        Assert.False(Flag(cpu, CF));
    }

    [Fact]
    public void Aas_adjusts_and_decrements_AH()
    {
        // AL = 0x0B: AAS → AX -= 6, AH -= 1, AL &= 0xF. AH 0x02 → 0x01, AL 0xB → 0x05. AF=CF=1.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x020B);
        LoadCode(bus, 0x3F);   // AAS
        cpu.Step();
        Assert.Equal(0x05u, cpu.GetRegister("AL"));
        Assert.Equal(0x01u, cpu.GetRegister("AH"));
        Assert.True(Flag(cpu, AF));
        Assert.True(Flag(cpu, CF));
    }

    [Fact]
    public void Aam_splits_AL_into_AH_AL_by_base()
    {
        // D4 0A = AAM (base 10). AL = 0x1D (29): AH = 29/10 = 2, AL = 29%10 = 9.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x001D);
        LoadCode(bus, 0xD4, 0x0A);
        cpu.Step();
        Assert.Equal(0x02u, cpu.GetRegister("AH"));
        Assert.Equal(0x09u, cpu.GetRegister("AL"));
        Assert.True(Flag(cpu, PF));   // 0x09 = 0b1001, even parity
    }

    [Fact]
    public void Aad_combines_AH_AL_by_base()
    {
        // D5 0A = AAD (base 10). AH=2, AL=9 → AL = (9 + 2*10) = 29 (0x1D), AH = 0.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0209);   // AH=2, AL=9
        LoadCode(bus, 0xD5, 0x0A);
        cpu.Step();
        Assert.Equal(0x1Du, cpu.GetRegister("AL"));
        Assert.Equal(0x00u, cpu.GetRegister("AH"));
    }

    [Fact]
    public void Aad_sets_ZF_when_result_zero()
    {
        // D5 0A = AAD. AH=0, AL=0 → AL = 0, ZF set.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x0000);
        LoadCode(bus, 0xD5, 0x0A);
        cpu.Step();
        Assert.Equal(0x00u, cpu.GetRegister("AX"));
        Assert.True(Flag(cpu, ZF));
    }
}
