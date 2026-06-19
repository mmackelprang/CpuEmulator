using CpuEmulator.Core;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Host;

/// <summary>
/// What the console host needs to run one board: the built <see cref="Machine"/>, the
/// <see cref="SimpleUart"/> the host bridges console stdin/stdout through (the board's
/// memory-mapped UART instance — the host wires <c>OnTransmit</c> and <c>FeedInput</c> to it),
/// and a one-line banner. Produced by <see cref="BoardRegistry"/>; consumed by Program.Main.
/// </summary>
public sealed record BootedBoard(Machine Machine, SimpleUart Uart, string Banner)
{
    /// <summary>A monitor engine over this board's CPU + program space, wired through
    /// Machine.Run so monitor g/s tick the scheduled peripherals (matching the retired
    /// Breadboard6502.NewMonitor wiring exactly).</summary>
    public MonitorEngine NewMonitor() =>
        new(Machine.Cpu, Machine.Space(AddressSpaceKind.Program), (IMonitorSupport)Machine.Cpu,
            Machine.Run);
}
