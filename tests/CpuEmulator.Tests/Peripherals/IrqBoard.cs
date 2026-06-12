using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

/// <summary>
/// RAM-vector dev board for interrupt testing. Unlike Breadboard6502 (ROM vectors),
/// this board has writable RAM everywhere outside the device pages — vectors at
/// $FFFE/$FFFF are settable by the test or via the monitor's 'm FFFE: ...' command.
///
/// Geometry: RAM $0000–$CFFF + UART $D000–$D0FF + IntervalTimer $D100–$D1FF +
/// RAM $D200–$FFFF (0x2E00 bytes; 0xD200 + 0x2E00 = 0x10000, page-aligned).
///
/// Reset() reads $FFFC/$FFFD = $0000 (RAM initialized to 0) — sessions set PC via
/// 'g $0200 ...' or directly. Reset's S=$FD and I-set are what each program's CLI undoes.
/// </summary>
internal sealed class IrqBoard
{
    private const uint LowRamLength = 0xD000;            // $0000–$CFFF (52 KiB)
    private const uint UartPageLength = 0x0100;          // $D000–$D0FF (1 page)
    private const uint TimerPageLength = 0x0100;         // $D100–$D1FF (1 page)
    private const uint HighRamStart = 0xD200;
    private const uint HighRamLength = 0x10000 - 0xD200; // $D200–$FFFF = 0x2E00

    public Machine Machine { get; }
    public SimpleUart Uart { get; }
    public IntervalTimer Timer { get; }

    private IrqBoard(Machine machine, SimpleUart uart, IntervalTimer timer)
    {
        Machine = machine;
        Uart = uart;
        Timer = timer;
    }

    public static IrqBoard Create()
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        var machine = Machine.Create("irq-board")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, LowRamLength)
            .WithPeripheral(AddressSpaceKind.Program, 0xD000, UartPageLength, uart)
            .WithPeripheral(AddressSpaceKind.Program, 0xD100, TimerPageLength, timer)
            .WithRam(AddressSpaceKind.Program, HighRamStart, HighRamLength)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();
        return new IrqBoard(machine, uart, timer);
    }

    public MonitorEngine NewMonitor() =>
        new(Machine.Cpu, Machine.Space(AddressSpaceKind.Program),
            (IMonitorSupport)Machine.Cpu, Machine.Run);
}
