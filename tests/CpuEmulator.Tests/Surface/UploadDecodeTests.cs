// tests/CpuEmulator.Tests/Surface/UploadDecodeTests.cs
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class UploadDecodeTests
{
    private static byte[] FrameOf(byte drive, byte format, byte[] body)
    {
        var f = new byte[5 + body.Length];
        f[0] = (byte)'D'; f[1] = (byte)'K'; f[2] = 0x01; f[3] = drive; f[4] = format;
        body.CopyTo(f.AsSpan(5));
        return f;
    }

    [Fact]
    public void Decodes_a_dsk_upload_into_drive_format_and_bytes()
    {
        byte[] body = { 1, 2, 3, 4 };
        Assert.True(FrameCodec.TryDecodeUpload(FrameOf(2, 1, body), out UploadFrame u));
        Assert.Equal(2, u.Drive);
        Assert.Equal(DiskFormat.Dsk, u.Format);
        Assert.Equal(body, u.Bytes);
    }

    [Fact]
    public void Decodes_po_and_woz_format_bytes()
    {
        Assert.True(FrameCodec.TryDecodeUpload(FrameOf(1, 2, new byte[] { 9 }), out UploadFrame po));
        Assert.Equal(DiskFormat.Po, po.Format);
        Assert.True(FrameCodec.TryDecodeUpload(FrameOf(1, 0, new byte[] { 9 }), out UploadFrame woz));
        Assert.Equal(DiskFormat.Woz, woz.Format);
    }

    [Fact]
    public void Rejects_a_non_DK_tag()
    {
        Assert.False(FrameCodec.TryDecodeUpload(new byte[] { (byte)'F', (byte)'B', 0x01, 1, 1, 0 }, out _));
    }

    [Fact]
    public void Rejects_a_bad_drive_or_format_or_short_frame()
    {
        Assert.False(FrameCodec.TryDecodeUpload(FrameOf(3, 1, new byte[] { 1 }), out _));   // drive 3
        Assert.False(FrameCodec.TryDecodeUpload(FrameOf(1, 9, new byte[] { 1 }), out _));   // format 9
        Assert.False(FrameCodec.TryDecodeUpload(new byte[] { (byte)'D', (byte)'K' }, out _)); // too short
    }
}
