using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class DiskInsertDecodeTests
{
    [Fact]
    public void Decodes_a_disk_insert_with_drive_and_id()
    {
        Assert.True(FrameCodec.TryDecodeDisk(
            "{\"action\":\"disk-insert\",\"drive\":2,\"id\":\"lib/DOS33.dsk\"}", out var cmd));
        Assert.False(cmd.Eject);
        Assert.Equal(2, cmd.Drive);
        Assert.Equal("lib/DOS33.dsk", cmd.Id);
    }

    [Fact]
    public void Decodes_a_disk_eject_with_drive()
    {
        Assert.True(FrameCodec.TryDecodeDisk("{\"action\":\"disk-eject\",\"drive\":1}", out var cmd));
        Assert.True(cmd.Eject);
        Assert.Equal(1, cmd.Drive);
    }

    [Fact]
    public void Rejects_a_key_event_json_so_the_key_path_is_never_shadowed()
    {
        Assert.False(FrameCodec.TryDecodeDisk("{\"action\":\"down\",\"code\":\"KeyA\",\"char\":\"a\"}", out _));
    }

    [Fact]
    public void Rejects_an_out_of_range_drive()
    {
        Assert.False(FrameCodec.TryDecodeDisk("{\"action\":\"disk-insert\",\"drive\":9,\"id\":\"lib/x.dsk\"}", out _));
    }
}
