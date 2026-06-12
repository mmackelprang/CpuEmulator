using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Host;

/// <summary>
/// The canonical 6502 breadboard composition:
///   RAM $0000–$CFFF (52 KiB), UART $D000–$D0FF (1 page), ROM $E000–$FFFF (8 KiB).
///   $D100–$DFFF is unmapped — open-bus reads 0xFF, writes ignored (non-strict defaults).
/// </summary>
public sealed class Breadboard6502
{
    public const uint UartBase = 0xD000;

    public Machine Machine { get; }
    public SimpleUart Uart { get; }
    public Mos6502Cpu Cpu => (Mos6502Cpu)Machine.Cpu;

    public Breadboard6502()
    {
        Uart = new SimpleUart();
        Machine = Machine.Create("breadboard6502")
            .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
            .WithPeripheral(AddressSpaceKind.Program, UartBase, 0x0100, Uart)
            .WithRom(AddressSpaceKind.Program, 0xE000, DemoRom.Build())
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();
    }

    /// <summary>Monitor engine wired through Machine.Run — monitor g/s tick the
    /// scheduler (the recorded chunk-4 intake; the seam is Task 3).</summary>
    public MonitorEngine NewMonitor() =>
        new(Cpu, Machine.Space(AddressSpaceKind.Program), Cpu); // Task 3 wires Machine.Run here
}
