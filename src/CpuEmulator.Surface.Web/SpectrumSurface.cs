using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// Composes the ZX Spectrum for the web surface — the analogue of <see cref="DemoBoardSurface"/>. Builds
/// the <see cref="SpectrumBoard"/> spec → a <see cref="Machine"/> via <see cref="BoardMachineFactory"/>,
/// resets it, and wires a <see cref="MachineHost"/> whose display + keyboard + audio are all the same
/// <see cref="SpectrumUla"/> instance (mapped on the Io port slot). The audio sink uses the Phase-1
/// 6-arg <see cref="MachineHost"/> ctor so the beeper plays via the WebSocket AU frames.
/// </summary>
public sealed record SpectrumSurface(Machine Machine, SpectrumUla Ula, MachineHost Host)
{
    public static SpectrumSurface Create(byte[] rom, Action<byte[]> frameSink, Action<byte[]> audioSink,
                                         ExecutionTier tier = ExecutionTier.Interpreter)
    {
        // Thread the build-time execution tier to the Z80 via SpectrumMachine.Build (interpreter by default,
        // JIT when the web server resolves --tier jit / ?tier=jit). The Spectrum has no coprocessor.
        Machine machine = SpectrumMachine.Build(rom, out SpectrumUla ula, tier);
        machine.Reset();
        var host = new MachineHost(machine, ula, ula, frameSink, ula, audioSink);
        return new SpectrumSurface(machine, ula, host);
    }
}
