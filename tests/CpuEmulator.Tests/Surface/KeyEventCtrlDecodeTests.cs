using CpuEmulator.Core;
using CpuEmulator.Surface.Web;
using Xunit;

namespace CpuEmulator.Tests.Surface;

/// <summary>D5 wire half: the inbound key JSON gains an optional <c>ctrl</c> boolean (the browser's
/// KeyboardEvent.ctrlKey). TryDecodeKey reads it into KeyEvent.Ctrl; absent -> false (every shipped
/// non-ctrl key event decodes unchanged).</summary>
public class KeyEventCtrlDecodeTests
{
    [Fact]
    public void Ctrl_true_decodes_to_KeyEvent_Ctrl()
    {
        Assert.True(FrameCodec.TryDecodeKey(
            "{\"action\":\"down\",\"code\":\"KeyB\",\"char\":\"b\",\"ctrl\":true}", out KeyEvent e));
        Assert.True(e.Ctrl);
        Assert.Equal(KeyCode.B, e.Key);
    }

    [Fact]
    public void Ctrl_false_decodes_to_Ctrl_false()
    {
        Assert.True(FrameCodec.TryDecodeKey(
            "{\"action\":\"down\",\"code\":\"KeyB\",\"char\":\"b\",\"ctrl\":false}", out KeyEvent e));
        Assert.False(e.Ctrl);
    }

    [Fact]
    public void Absent_ctrl_defaults_to_false()
    {
        Assert.True(FrameCodec.TryDecodeKey(
            "{\"action\":\"down\",\"code\":\"KeyA\",\"char\":\"a\"}", out KeyEvent e));
        Assert.False(e.Ctrl);   // the shipped non-ctrl shape is unchanged
    }
}
