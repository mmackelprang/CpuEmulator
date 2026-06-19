using CpuEmulator.Core;
using CpuEmulator.Host;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Machines;

public class Breadboard6502BoardTests
{
    private static (Machine Machine, SimpleUart Uart, IntervalTimer Timer) NewBoard()
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        BoardSpec spec = Breadboard6502Board.Spec(DemoRom.Build(), uart, timer);
        return (BoardMachineFactory.Build(spec), uart, timer);
    }

    [Fact]
    public void Ram_is_writable_across_the_low_52k()
    {
        var (machine, _, _) = NewBoard();
        var space = machine.Space(AddressSpaceKind.Program);
        space.Write8(0xCFFF, 0xCD);
        Assert.Equal(0xCD, space.Read8(0xCFFF));
    }

    [Fact]
    public void Uart_status_reads_0x02_when_empty()
    {
        var (machine, _, _) = NewBoard();
        Assert.Equal(0x02u, machine.Space(AddressSpaceKind.Program).Read8(0xD001));
    }

    [Fact]
    public void Timer_ctrl_reads_zero_at_boot()
    {
        var (machine, _, _) = NewBoard();
        Assert.Equal(0x00u, machine.Space(AddressSpaceKind.Program).Read8(0xD100));
    }

    [Fact]
    public void Open_bus_at_D200_reads_0xFF()
    {
        var (machine, _, _) = NewBoard();
        Assert.Equal(0xFFu, machine.Space(AddressSpaceKind.Program).Read8(0xD200));
    }

    [Fact]
    public void Rom_at_E000_is_the_demo_rom_first_byte()
    {
        var (machine, _, _) = NewBoard();
        // DemoRom's first instruction is LDX #$00 (opcode 0xA2).
        Assert.Equal(0xA2, machine.Space(AddressSpaceKind.Program).Read8(0xE000));
    }

    [Fact]
    public void Spec_validates_clean()
    {
        BoardSpec spec = Breadboard6502Board.Spec(DemoRom.Build(), new SimpleUart(), new IntervalTimer());
        Assert.Empty(BoardSpecValidator.Validate(spec));
    }
}
