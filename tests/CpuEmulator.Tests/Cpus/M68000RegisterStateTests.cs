using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>M4.1 — the 68000 register-file proof (ADR 0003 Decision 1). The register state exists and is
/// correct: D0–D7/A0–A6/USP/SSP/PC round-trip full 32-bit values; A7 is a mode-selected VIEW over USP/SSP
/// (the SR S-bit selects); the SR/CCR split reads/writes. NO instruction executes — this is state only.</summary>
public class M68000RegisterStateTests
{
    private static M68000Cpu NewCpu()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 24);
        bus.MapMemory(0, new byte[0x10000], writable: true);
        return new M68000Cpu(bus);
    }

    [Theory]
    [InlineData("D0")] [InlineData("D7")]
    [InlineData("A0")] [InlineData("A6")]
    [InlineData("USP")] [InlineData("SSP")] [InlineData("PC")]
    public void Register_round_trips_a_full_32bit_value(string reg)
    {
        var cpu = NewCpu();
        cpu.SetRegister(reg, 0xDEAD_BEEFul);
        Assert.Equal(0xDEAD_BEEFul, cpu.GetRegister(reg));   // a ushort field would truncate to 0xBEEF
    }

    [Fact]
    public void A7_is_not_a_named_introspection_register()
    {
        // Decision D2: A7 is a C# convenience view, NOT a spec register. The TomHarte schema names
        // usp/ssp, never a7, so GetRegister("A7") is intentionally unknown.
        var cpu = NewCpu();
        Assert.Throws<System.ArgumentException>(() => cpu.GetRegister("A7"));
    }

    [Fact]
    public void A7_reads_USP_in_user_mode()
    {
        var cpu = NewCpu();
        cpu.SetSupervisorMode(false);                 // user mode (SR.S = 0)
        cpu.SetRegister("USP", 0x0001_0000ul);
        cpu.SetRegister("SSP", 0x0008_0000ul);
        Assert.Equal(0x0001_0000u, cpu.A7);           // A7 == USP in user mode
    }

    [Fact]
    public void A7_reads_SSP_in_supervisor_mode()
    {
        var cpu = NewCpu();
        cpu.SetSupervisorMode(true);                  // supervisor mode (SR.S = 1)
        cpu.SetRegister("USP", 0x0001_0000ul);
        cpu.SetRegister("SSP", 0x0008_0000ul);
        Assert.Equal(0x0008_0000u, cpu.A7);           // A7 == SSP in supervisor mode
    }

    [Fact]
    public void Writing_A7_in_user_mode_targets_USP_only()
    {
        var cpu = NewCpu();
        cpu.SetSupervisorMode(false);
        cpu.SetRegister("SSP", 0x0008_0000ul);
        cpu.A7 = 0x0002_0000u;
        Assert.Equal(0x0002_0000ul, cpu.GetRegister("USP"));   // USP got the write
        Assert.Equal(0x0008_0000ul, cpu.GetRegister("SSP"));   // SSP untouched
    }

    [Fact]
    public void Writing_A7_in_supervisor_mode_targets_SSP_only()
    {
        var cpu = NewCpu();
        cpu.SetSupervisorMode(true);
        cpu.SetRegister("USP", 0x0001_0000ul);
        cpu.A7 = 0x0009_0000u;
        Assert.Equal(0x0009_0000ul, cpu.GetRegister("SSP"));   // SSP got the write
        Assert.Equal(0x0001_0000ul, cpu.GetRegister("USP"));   // USP untouched
    }

    [Fact]
    public void SupervisorMode_reflects_the_SR_S_bit()
    {
        var cpu = NewCpu();
        cpu.SetRegister("SR", 0x2000ul);              // S-bit (bit 13) set
        Assert.True(cpu.SupervisorMode);
        cpu.SetRegister("SR", 0x0000ul);              // S-bit clear
        Assert.False(cpu.SupervisorMode);
    }

    [Fact]
    public void SR_CCR_split_round_trips()
    {
        var cpu = NewCpu();
        // SR = 0x271F: S(13)+I2,I1,I0(10,9,8 = mask 7) in the system byte; CCR low byte 0x1F = X N Z V C all set.
        cpu.SetRegister("SR", 0x271Ful);
        Assert.Equal(0x271Ful, cpu.GetRegister("SR"));        // the full 16-bit SR round-trips
        Assert.Equal((byte)0x1F, cpu.Ccr);                    // the CCR is the SR low byte
    }
}
