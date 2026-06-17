using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>M5.1 — the 8086 register-file + FLAGS + 20-bit LE bus proof (ADR 0005). The register state
/// exists and is correct: AX/BX/CX/DX are pair-views over their 8-bit halves (writing a half preserves
/// the other — the partial-write hazard); SP/BP/SI/DI/IP/FLAGS and the four segment registers round-trip
/// full 16-bit values; a 16-bit field truncates a wider write; and the 20-bit-configured bus addresses
/// the top byte at 0xFFFFF. NO instruction executes — this is state only.</summary>
public class M8086RegisterStateTests
{
    private static M8086Cpu NewCpu()
    {
        // 20-bit (1 MB) little-endian program space. Map the whole 1 MB as RAM (4096 256-byte pages)
        // so the byte round-trip at the top address 0xFFFFF lands in mapped memory.
        var bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        return new M8086Cpu(bus);
    }

    private static AddressSpace NewBus()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        return bus;
    }

    [Fact]
    public void AX_write_updates_both_halves()
    {
        var cpu = NewCpu();
        cpu.SetRegister("AX", 0x1234);
        Assert.Equal(0x12u, cpu.GetRegister("AH"));
        Assert.Equal(0x34u, cpu.GetRegister("AL"));
        Assert.Equal(0x1234u, cpu.GetRegister("AX"));
    }

    [Theory]
    [InlineData("AX", "AH", "AL")]
    [InlineData("BX", "BH", "BL")]
    [InlineData("CX", "CH", "CL")]
    [InlineData("DX", "DH", "DL")]
    public void Pair_view_decomposes_into_its_halves(string pair, string high, string low)
    {
        var cpu = NewCpu();
        cpu.SetRegister(pair, 0xABCD);
        Assert.Equal(0xABu, cpu.GetRegister(high));
        Assert.Equal(0xCDu, cpu.GetRegister(low));
        Assert.Equal(0xABCDu, cpu.GetRegister(pair));
    }

    [Theory]
    [InlineData("AX", "AH", "AL")]
    [InlineData("BX", "BH", "BL")]
    [InlineData("CX", "CH", "CL")]
    [InlineData("DX", "DH", "DL")]
    public void Writing_the_low_half_preserves_the_high_half(string pair, string high, string low)
    {
        // The partial-write hazard: writing AL must leave AH intact.
        var cpu = NewCpu();
        cpu.SetRegister(pair, 0xFFFF);
        cpu.SetRegister(low, 0x00);
        Assert.Equal(0xFFu, cpu.GetRegister(high));
        Assert.Equal(0xFF00u, cpu.GetRegister(pair));
    }

    [Theory]
    [InlineData("AX", "AH", "AL")]
    [InlineData("BX", "BH", "BL")]
    [InlineData("CX", "CH", "CL")]
    [InlineData("DX", "DH", "DL")]
    public void Writing_the_high_half_preserves_the_low_half(string pair, string high, string low)
    {
        var cpu = NewCpu();
        cpu.SetRegister(pair, 0xFFFF);
        cpu.SetRegister(high, 0x00);
        Assert.Equal(0xFFu, cpu.GetRegister(low));
        Assert.Equal(0x00FFu, cpu.GetRegister(pair));
    }

    [Theory]
    [InlineData("SP")]
    [InlineData("BP")]
    [InlineData("SI")]
    [InlineData("DI")]
    public void SP_BP_SI_DI_round_trip(string reg)
    {
        // These are full 16-bit registers, NOT byte-decomposable.
        var cpu = NewCpu();
        cpu.SetRegister(reg, 0xBEEF);
        Assert.Equal(0xBEEFu, cpu.GetRegister(reg));
    }

    [Theory]
    [InlineData("CS")]
    [InlineData("DS")]
    [InlineData("ES")]
    [InlineData("SS")]
    public void Segment_registers_round_trip(string reg)
    {
        var cpu = NewCpu();
        cpu.SetRegister(reg, 0xF000);
        Assert.Equal(0xF000u, cpu.GetRegister(reg));
    }

    [Fact]
    public void IP_round_trips()
    {
        var cpu = NewCpu();
        cpu.SetRegister("IP", 0xCAFE);
        Assert.Equal(0xCAFEu, cpu.GetRegister("IP"));
    }

    [Fact]
    public void FLAGS_round_trips()
    {
        var cpu = NewCpu();
        cpu.SetRegister("FLAGS", 0xF2C3);
        Assert.Equal(0xF2C3u, cpu.GetRegister("FLAGS"));
    }

    [Fact]
    public void Register_write_truncates_to_16_bits()
    {
        // The backing field is a ushort, so the generated SetRegister truncates with unchecked((ushort)value).
        var cpu = NewCpu();
        cpu.SetRegister("SI", 0x1_0000);
        Assert.Equal(0x0000u, cpu.GetRegister("SI"));
    }

    [Fact]
    public void Bus_addresses_the_top_byte_of_the_20bit_space()
    {
        // 0xFFFFF is the highest address in the 20-bit (1 MB) space; the round-trip proves the bus is
        // configured for 20 address bits and the top page is mapped.
        var bus = NewBus();
        _ = new M8086Cpu(bus);
        bus.Write8(0xFFFFF, 0xAB);
        Assert.Equal((byte)0xAB, bus.Read8(0xFFFFF));
    }

    [Fact]
    public void Bus_wraps_above_the_20bit_space()
    {
        // NOTE: this exercises AddressSpace's OWN 20-bit mask (AddressMask = (1 << 20) - 1), proving the
        // bus the 8086 host wires is configured for 1 MB — it does NOT go through M8086Cpu's address path
        // (the CPU-level seg<<4 + offset crossing 0xFFFFF is M5.3). The constructor call below is only an
        // arg-validation smoke (the bus is exercised directly).
        var bus = NewBus();
        _ = new M8086Cpu(bus);
        bus.Write8(0x100000, 0x5A);
        Assert.Equal((byte)0x5A, bus.Read8(0x00000));
    }

    [Fact]
    public void RegisterBits_reports_the_field_widths()
    {
        var cpu = NewCpu();
        var monitor = (IMonitorSupport)cpu;
        Assert.Equal(16, monitor.RegisterBits("AX"));
        Assert.Equal(8, monitor.RegisterBits("AL"));
    }
}
