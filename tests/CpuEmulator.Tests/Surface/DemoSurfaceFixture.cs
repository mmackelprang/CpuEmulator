using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Surface;

/// <summary>Builds the SP0 demo board for tests — the same composition the web surface uses, minus
/// the WebSocket. Exposes the three device handles so tests can pull RGBA, post keys, and seed the disk.</summary>
public sealed record DemoSurfaceFixture(
    Machine Machine, DemoFramebuffer Framebuffer, DemoKeyboard Keyboard, DemoDisk Disk)
{
    public static DemoSurfaceFixture Build()
    {
        var fb = new DemoFramebuffer();
        var kbd = new DemoKeyboard();
        // Seed disk sector 0 with a recognizable first byte for the acceptance assertion.
        var image = new byte[256 * 2];
        image[0] = 0x5A;
        var disk = new DemoDisk(new DiskImage(image, sectorSize: 256, isReadOnly: false));

        BoardSpec spec = DemoBoard.Spec(DemoBoardRom.Build(), fb, kbd, disk);
        Machine machine = BoardMachineFactory.Build(spec);
        return new DemoSurfaceFixture(machine, fb, kbd, disk);
    }

    /// <summary>Build a minimal Machine wrapping ONLY the framebuffer (for DemoFramebufferTests'
    /// FrameReady vblank test) — a one-slot board so the scheduler advances and raises the tick.</summary>
    public static Machine BuildMachineWith(DemoFramebuffer fb)
    {
        // Reuse the full demo board; the framebuffer's vblank fires regardless of the other devices.
        return Build() is var f && ReferenceEquals(f.Framebuffer, fb)
            ? f.Machine
            : BoardMachineFactory.Build(DemoBoard.Spec(DemoBoardRom.Build(), fb, new DemoKeyboard(),
                new DemoDisk(new DiskImage(new byte[256], 256, false))));
    }
}
