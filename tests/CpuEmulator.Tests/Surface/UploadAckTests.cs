// tests/CpuEmulator.Tests/Surface/UploadAckTests.cs
using System.Text;
using System.Text.Json;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class UploadAckTests
{
    [Fact]
    public void Ack_is_an_ST_prefixed_json_carrying_drive_ok_and_message()
    {
        byte[] frame = FrameCodec.EncodeUploadAck(2, new UploadResult(false, "That image looks corrupt"));
        string text = Encoding.UTF8.GetString(frame);
        Assert.StartsWith("ST ", text);

        using JsonDocument doc = JsonDocument.Parse(text["ST ".Length..]);
        JsonElement up = doc.RootElement.GetProperty("upload");
        Assert.Equal(2, up.GetProperty("drive").GetInt32());
        Assert.False(up.GetProperty("ok").GetBoolean());
        Assert.Equal("That image looks corrupt", up.GetProperty("message").GetString());
    }

    [Fact]
    public void A_success_ack_has_ok_true_and_empty_message()
    {
        byte[] frame = FrameCodec.EncodeUploadAck(1, new UploadResult(true, ""));
        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(frame)["ST ".Length..]);
        JsonElement up = doc.RootElement.GetProperty("upload");
        Assert.True(up.GetProperty("ok").GetBoolean());
        Assert.Equal("", up.GetProperty("message").GetString());
    }
}
