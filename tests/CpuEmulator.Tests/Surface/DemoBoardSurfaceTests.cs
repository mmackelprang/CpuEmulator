using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class DemoBoardSurfaceTests
{
    [Fact]
    public void Create_builds_a_reset_machine_and_a_host_that_pushes_a_frame()
    {
        var frames = new List<byte[]>();
        DemoBoardSurface surface = DemoBoardSurface.Create(frames.Add);

        surface.Host.Step(100_000); // past one vblank

        Assert.NotEmpty(frames);
        Assert.Equal("demo", surface.Machine.Name);
    }

    [Fact]
    public void Disk_is_seeded_so_the_demo_can_read_sector_zero()
    {
        DemoBoardSurface surface = DemoBoardSurface.Create(_ => { });
        surface.Host.RunHeadless(20_000, 5_000);

        var rgba = new uint[surface.Framebuffer.Width * surface.Framebuffer.Height];
        surface.Framebuffer.RenderInto(rgba);
        // The seeded disk byte (0x5A) lands at VRAM $8101 -> rgba index 0x0101.
        Assert.Equal(0xFF5A5A5Au, rgba[0x0101]);
    }
}
