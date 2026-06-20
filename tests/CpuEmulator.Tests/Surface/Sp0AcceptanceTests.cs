using CpuEmulator.Core;

namespace CpuEmulator.Tests.Surface;

/// <summary>
/// The SP0 acceptance gate (design spec §6), headless/fast — no browser, no throttle. Runs the
/// DemoBoard via the Machine and asserts the three device contracts end-to-end: (a) RenderInto
/// yields the expected gradient test pattern, (b) a synthetic PostKey changes VRAM, (c) a disk
/// ReadSector surfaces image bytes to the guest. Un-fakeable: the assertions read the real RGBA
/// the chip produced, the real VRAM the guest wrote, and the real disk byte the guest fetched.
/// </summary>
[Trait("Category", "UAT")]
public class Sp0AcceptanceTests
{
    [Fact]
    public void Demo_proves_display_keyboard_and_disk_end_to_end()
    {
        DemoSurfaceFixture fix = DemoSurfaceFixture.Build();
        fix.Machine.Reset();

        // Run enough cycles for the ROM to paint the pattern, read the disk byte, and enter the
        // keyboard poll loop. The pattern (256 px) + disk read complete in well under 5,000 cycles.
        fix.Machine.Run(20_000);

        var rgba = new uint[fix.Framebuffer.Width * fix.Framebuffer.Height];
        fix.Framebuffer.RenderInto(rgba);

        // (a) DISPLAY: the gradient test pattern — VRAM[i] == i for i in [0,256), grayscale palette.
        for (int i = 0; i < 256; i++)
        {
            uint expected = 0xFF000000u | (uint)(i << 16) | (uint)(i << 8) | (uint)i;
            Assert.Equal(expected, rgba[i]);
        }

        // (c) DISK: the guest read sector 0 (first byte 0x5A) and painted it at VRAM offset $8100-$8000
        //     = 0x0100 (DiskCell $8101 -> offset 0x0101). Index 0x0101 in the rgba buffer.
        Assert.Equal(0xFF5A5A5Au, rgba[0x0101]);

        // (b) KEYBOARD: synthetically post a key; run; assert it landed at the echo cell ($8100 -> 0x0100).
        fix.Keyboard.PostKey(new KeyEvent(KeyAction.Down, KeyCode.K, 'K'));
        fix.Machine.Run(20_000);

        fix.Framebuffer.RenderInto(rgba);
        uint k = 0xFF000000u | ((uint)'K' << 16) | ((uint)'K' << 8) | (uint)'K';
        Assert.Equal(k, rgba[0x0100]);
    }
}
