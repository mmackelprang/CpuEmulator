using CpuEmulator.Core;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class FrameCodecTests
{
    [Fact]
    public void EncodeFrame_writes_header_then_little_endian_pixels()
    {
        uint[] pixels = [0xFF0000FFu, 0xFF00FF00u]; // 2x1
        byte[] frame = FrameCodec.EncodeFrame(2, 1, pixels);

        // header: 'F','B',version,reserved, w_lo,w_hi, h_lo,h_hi
        Assert.Equal((byte)'F', frame[0]);
        Assert.Equal((byte)'B', frame[1]);
        Assert.Equal(0x01, frame[2]);
        Assert.Equal(0x00, frame[3]);
        Assert.Equal(2, frame[4] | (frame[5] << 8)); // width
        Assert.Equal(1, frame[6] | (frame[7] << 8)); // height
        // pixel 0 little-endian
        Assert.Equal(0xFF, frame[8]);  // 0x000000FF -> LE bytes FF 00 00 FF
        Assert.Equal(0x00, frame[9]);
        Assert.Equal(0x00, frame[10]);
        Assert.Equal(0xFF, frame[11]);
        Assert.Equal(8 + 2 * 4, frame.Length);
    }

    [Theory]
    [InlineData("{\"action\":\"down\",\"code\":\"KeyA\",\"char\":\"a\"}", KeyAction.Down, KeyCode.A, 'a')]
    [InlineData("{\"action\":\"up\",\"code\":\"KeyA\",\"char\":\"\"}", KeyAction.Up, KeyCode.A, null)]
    [InlineData("{\"action\":\"down\",\"code\":\"Enter\",\"char\":\"\"}", KeyAction.Down, KeyCode.Enter, null)]
    [InlineData("{\"action\":\"down\",\"code\":\"Space\",\"char\":\" \"}", KeyAction.Down, KeyCode.Space, ' ')]
    public void TryDecodeKey_parses_a_json_key_event(string json, KeyAction action, KeyCode key, char? ch)
    {
        Assert.True(FrameCodec.TryDecodeKey(json, out KeyEvent e));
        Assert.Equal(action, e.Action);
        Assert.Equal(key, e.Key);
        Assert.Equal(ch, e.Char);
    }

    [Fact]
    public void TryDecodeKey_maps_an_unknown_dom_code_to_None()
    {
        Assert.True(FrameCodec.TryDecodeKey("{\"action\":\"down\",\"code\":\"F13\",\"char\":\"\"}", out KeyEvent e));
        Assert.Equal(KeyCode.None, e.Key);
    }

    [Fact]
    public void TryDecodeKey_rejects_malformed_json()
    {
        Assert.False(FrameCodec.TryDecodeKey("not json", out _));
    }
}
