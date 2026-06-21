// tests/CpuEmulator.Tests/Surface/UploadValidatorTests.cs
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class UploadValidatorTests
{
    private static byte[] DskBytes() => new byte[DiskImageFactory.DskBytes];     // exactly 143,360

    [Fact]
    public void A_correct_length_dsk_validates()
    {
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Dsk, DskBytes()));
        Assert.True(r.Ok);
        Assert.Equal("", r.Message);
    }

    [Fact]
    public void A_correct_length_po_validates()
    {
        Assert.True(UploadValidator.Validate(new UploadFrame(1, DiskFormat.Po, DskBytes())).Ok);
    }

    [Fact]
    public void A_wrong_length_dsk_is_rejected_as_corrupt()
    {
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Dsk, new byte[100]));
        Assert.False(r.Ok);
        Assert.Equal("That image looks corrupt", r.Message);
    }

    [Fact]
    public void An_empty_body_is_rejected()
    {
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Dsk, Array.Empty<byte>()));
        Assert.False(r.Ok);
        Assert.Equal("That file is empty", r.Message);
    }

    [Fact]
    public void A_woz_with_bad_magic_is_corrupt()
    {
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Woz, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.False(r.Ok);
        Assert.Equal("That image looks corrupt", r.Message);
    }

    [Fact]
    public void A_woz_with_good_magic_is_rejected_as_not_yet_supported()
    {
        // WOZ2 magic + the 0xFF byte, padded to a plausible header length.
        var woz = new byte[16];
        woz[0] = 0x57; woz[1] = 0x4F; woz[2] = 0x5A; woz[3] = 0x32; woz[4] = 0xFF;   // "WOZ2"+FF
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Woz, woz));
        Assert.False(r.Ok);
        Assert.Equal(".woz upload isn't supported yet — use .dsk or .po", r.Message);
    }
}
