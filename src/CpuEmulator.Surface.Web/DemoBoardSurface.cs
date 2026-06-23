using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// Composes the SP0 demo board for the web surface — the web analogue of the monitor host's
/// BootedBoard. Builds the three devices, compiles the <see cref="DemoBoard"/> spec to a
/// <see cref="Machine"/> via <see cref="BoardMachineFactory"/>, resets it, and wires a
/// <see cref="MachineHost"/> to the supplied frame sink. The disk is seeded with a recognizable
/// sector 0 so the demo program (and the acceptance test) have a byte to surface.
/// </summary>
public sealed record DemoBoardSurface(
    Machine Machine, DemoFramebuffer Framebuffer, DemoKeyboard Keyboard, DemoDisk Disk, MachineHost Host)
{
    public static DemoBoardSurface Create(Action<byte[]> frameSink,
                                          ExecutionTier tier = ExecutionTier.Interpreter)
    {
        var fb = new DemoFramebuffer();
        var kbd = new DemoKeyboard();
        var image = new byte[256 * 2];
        image[0] = 0x5A; // recognizable sector-0 first byte
        var disk = new DemoDisk(new DiskImage(image, sectorSize: 256, isReadOnly: false));

        BoardSpec spec = DemoBoard.Spec(DemoBoardRom.Build(), fb, kbd, disk);
        // Thread the build-time execution tier to the demo board's CPU (interpreter by default, JIT when the
        // web server resolves --tier jit / ?tier=jit). The demo board is single-CPU — no coprocessor.
        Machine machine = BoardMachineFactory.Build(spec, tier);
        machine.Reset();

        var host = new MachineHost(machine, fb, kbd, frameSink);
        return new DemoBoardSurface(machine, fb, kbd, disk, host);
    }
}
