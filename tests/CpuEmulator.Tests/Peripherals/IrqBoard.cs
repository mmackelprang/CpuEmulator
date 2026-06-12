using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

/// <summary>
/// RAM-vector dev board for interrupt testing. Unlike Breadboard6502 (ROM vectors),
/// this board has writable RAM everywhere — vectors at $FFFE/$FFFF are settable by
/// the test or via the monitor's 'm FFFE: ...' command.
///
/// Task 4 geometry: RAM $0000–$CFFF + UART $D000–$D0FF + RAM $D100–$FFFF.
/// Task 5 moves the RAM upper boundary to $D200 (adding the timer at $D100).
/// Both layouts are page-legal; the fixture is internal, not pinned externally.
///
/// Reset() reads $FFFC/$FFFD = $0000 (RAM initialized to 0) — sessions set PC via
/// 'g $0200 ...' or directly. Reset's S=$FD and I-set are what each program's CLI undoes.
/// </summary>
internal sealed class IrqBoard
{
    private const uint RamLow = 0xD000;          // $0000–$CFFF (52 KiB)
    private const uint UartPage = 0x0100;         // $D000–$D0FF (1 page)
    private const uint RamHighStart = 0xD100;
    private const uint RamHighLen = 0x10000 - 0xD100; // $D100–$FFFF = 0x2F00

    public Machine Machine { get; }
    public SimpleUart Uart { get; }

    private IrqBoard(Machine machine, SimpleUart uart)
    {
        Machine = machine;
        Uart = uart;
    }

    public static IrqBoard Create()
    {
        var uart = new SimpleUart();
        var machine = Machine.Create("irq-board")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, RamLow)
            .WithPeripheral(AddressSpaceKind.Program, 0xD000, UartPage, uart)
            .WithRam(AddressSpaceKind.Program, RamHighStart, RamHighLen)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();
        return new IrqBoard(machine, uart);
    }

    public MonitorEngine NewMonitor() =>
        new(Machine.Cpu, Machine.Space(AddressSpaceKind.Program),
            (IMonitorSupport)Machine.Cpu, Machine.Run);
}
