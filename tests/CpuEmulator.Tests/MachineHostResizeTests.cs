using CpuEmulator.Core;
using CpuEmulator.Surface.Web;
using CpuEmulator.Tests.TestDoubles;

namespace CpuEmulator.Tests;

public class MachineHostResizeTests
{
    // A fixed-size display source that raises FrameReady on demand and fills a known color.
    private sealed class FakeDisplay(int width, int height, uint fill) : IDisplayDevice
    {
        public int Width => width;
        public int Height => height;
        public void RenderInto(Span<uint> rgba) => rgba[..(Width * Height)].Fill(fill);
        public event Action? FrameReady;
        public void RaiseFrame() => FrameReady?.Invoke();
    }

    private sealed class NoKeyboard : IKeyboardSink
    {
        public void PostKey(in KeyEvent e) { }
    }

    // A trivial real machine the host can Run (a FakeCpu does nothing meaningful; frames come from the
    // display FrameReady the test raises, not from the CPU).
    private static Machine TrivialMachine() =>
        Machine.Create("host-resize")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(_ => new FakeCpu())
            .Build();

    private static (int width, int height, int payloadLen) DecodeFb(byte[] frame)
    {
        // FB header: 'F','B', ver, reserved, u16 width LE, u16 height LE, then width*height*4 RGBA bytes.
        Assert.Equal((byte)'F', frame[0]);
        Assert.Equal((byte)'B', frame[1]);
        int w = frame[4] | (frame[5] << 8);
        int h = frame[6] | (frame[7] << 8);
        return (w, h, frame.Length);
    }

    [Fact]
    public void Switching_the_active_source_makes_the_host_re_pull_at_the_new_size()
    {
        var small = new FakeDisplay(280, 192, 0xFF111111);
        var large = new FakeDisplay(720, 216, 0xFF222222);
        var mux = new DisplayMultiplexer([small, large], initialActive: 0);

        byte[]? frame = null;
        var host = new MachineHost(TrivialMachine(), mux, new NoKeyboard(), f => frame = f);

        // 1) The small source's vblank -> Step renders a 280x192 frame.
        small.RaiseFrame();
        host.Step(1);
        Assert.NotNull(frame);
        var (w1, h1, len1) = DecodeFb(frame!);
        Assert.Equal(280, w1);
        Assert.Equal(192, h1);
        // The header is 8 bytes; the payload is width*height*4 RGBA bytes (grounded against FrameCodec).
        Assert.Equal(8 + 280 * 192 * 4, len1);

        // 2) Switch the active source (fires FrameReady) -> Step re-pulls at 720x216 (the host re-sized).
        mux.SetActive(1);
        host.Step(1);
        var (w2, h2, len2) = DecodeFb(frame!);
        Assert.Equal(720, w2);          // the host followed the new geometry...
        Assert.Equal(216, h2);
        Assert.Equal(8 + 720 * 216 * 4, len2);   // ...and the buffer re-sized (no truncation/overflow)
        Assert.True(len2 > len1);       // the larger source yields a larger frame (the re-size happened)
    }

    [Fact]
    public void A_single_source_host_is_unchanged_the_buffer_never_re_sizes()
    {
        // The single-display path (every shipped surface): one fixed-size source, frames always the same
        // geometry, no reallocation. This is the byte-for-byte-unchanged regression for the host re-size.
        var only = new FakeDisplay(256, 192, 0xFF333333);

        var frames = new List<byte[]>();
        var host = new MachineHost(TrivialMachine(), only, new NoKeyboard(), frames.Add);

        for (int i = 0; i < 5; i++) { only.RaiseFrame(); host.Step(1); }

        Assert.Equal(5, frames.Count);
        foreach (byte[] f in frames)
        {
            var (w, h, len) = DecodeFb(f);
            Assert.Equal(256, w);
            Assert.Equal(192, h);
            Assert.Equal(8 + 256 * 192 * 4, len);   // every frame identical geometry — no re-size ever
        }
    }
}
