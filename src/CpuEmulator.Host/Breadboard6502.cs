using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Host;

/// <summary>
/// The canonical 6502 breadboard composition (v2 memory map):
///   RAM $0000–$CFFF (52 KiB) · UART $D000–$D0FF (1 page; DATA $D000, STATUS $D001,
///   CTRL $D002) · IntervalTimer $D100–$D1FF (1 page; CTRL $D100, PERIODL $D101,
///   PERIODH $D102, STATUS $D103; mirrors every 4 bytes) · $D200–$DFFF unmapped
///   (open-bus reads 0xFF, writes ignored — non-strict defaults) · ROM $E000–$FFFF (8 KiB).
/// The demo ROM is unchanged: all vectors → $E000, so enabling the timer's IRQ with
/// I clear restarts the demo — poll STATUS interactively, or build a RAM-vector board
/// for handler experiments (see docs/user-guide/building-machines.md).
/// </summary>
public sealed class Breadboard6502
{
    public const uint UartBase = 0xD000;
    public const uint TimerBase = 0xD100;

    public Machine Machine { get; }
    public SimpleUart Uart { get; }
    public IntervalTimer Timer { get; }
    public Mos6502Cpu Cpu => (Mos6502Cpu)Machine.Cpu;

    public Breadboard6502()
    {
        Uart = new SimpleUart();
        Timer = new IntervalTimer();
        Machine = Machine.Create("breadboard6502")
            .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
            .WithPeripheral(AddressSpaceKind.Program, UartBase, 0x0100, Uart)
            .WithPeripheral(AddressSpaceKind.Program, TimerBase, 0x0100, Timer)
            .WithRom(AddressSpaceKind.Program, 0xE000, DemoRom.Build())
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();
    }

    /// <summary>Monitor engine wired through Machine.Run — monitor g/s tick the
    /// scheduler (the recorded chunk-4 intake).</summary>
    public MonitorEngine NewMonitor() =>
        new(Cpu, Machine.Space(AddressSpaceKind.Program), Cpu, Machine.Run);
}
