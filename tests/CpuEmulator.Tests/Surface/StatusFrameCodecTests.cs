using System.Text;
using System.Text.Json;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class StatusFrameCodecTests
{
    [Fact]
    public void EncodeStatus_is_an_ST_prefixed_json_text_frame_carrying_every_field()
    {
        var status = new MachineStatus(
            Board: "Apple ][+ SoftCard",
            Asset: "softcard-cpm-videx",
            Mode: "Videx 80×24 · CP/M",
            Drives:
            [
                new DriveStatus(MotorOn: true, Label: "CPM.dsk"),
                new DriveStatus(MotorOn: false, Label: "—"),
            ]);

        byte[] frame = FrameCodec.EncodeStatus(status);
        string text = Encoding.UTF8.GetString(frame);

        // The wire stays "ST " + a JSON body (the client routes ALL text to handleStatusText; the
        // "ST " prefix is the existing contract app.js already gates on).
        Assert.StartsWith("ST ", text);

        using JsonDocument doc = JsonDocument.Parse(text["ST ".Length..]);
        JsonElement root = doc.RootElement;
        Assert.Equal("Apple ][+ SoftCard", root.GetProperty("board").GetString());
        Assert.Equal("softcard-cpm-videx", root.GetProperty("asset").GetString());
        Assert.Equal("Videx 80×24 · CP/M", root.GetProperty("mode").GetString());

        JsonElement drives = root.GetProperty("drives");
        Assert.Equal(2, drives.GetArrayLength());
        Assert.True(drives[0].GetProperty("motor").GetBoolean());
        Assert.Equal("CPM.dsk", drives[0].GetProperty("label").GetString());
        Assert.False(drives[1].GetProperty("motor").GetBoolean());
        Assert.Equal("—", drives[1].GetProperty("label").GetString());
    }

    [Fact]
    public void EncodeStatus_equal_snapshots_produce_equal_bytes_so_change_detection_is_byte_compare()
    {
        var a = new MachineStatus("Apple ][+", "apple", "TEXT · 40×24 · page 1",
            [new DriveStatus(false, "—")]);
        var b = new MachineStatus("Apple ][+", "apple", "TEXT · 40×24 · page 1",
            [new DriveStatus(false, "—")]);

        Assert.Equal(FrameCodec.EncodeStatus(a), FrameCodec.EncodeStatus(b));
    }
}
