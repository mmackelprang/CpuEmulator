using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using CpuEmulator.Surface.Web;
using WebProgram = CpuEmulator.Surface.Web.Program;

namespace CpuEmulator.Tests.Surface;

/// <summary>The PR-S gate: a binary DK frame with a valid .dsk payload inserts into drive N; a bad
/// length is rejected (an error ack); and the single-text-frame (key) protocol is unaffected.
///
/// NOTE: this class never mutates the process-global <c>CPUEMULATOR_TESTVECTORS</c> env var — that var is
/// read live by the parallel TomHarte/Klaus vector suites, so mutating it (as the plan's literal version
/// did via <c>Environment.SetEnvironmentVariable</c>) would corrupt their vector resolution under the
/// assembly's parallel test collections (exactly the flake PR-R hit and fixed). Following R's pattern, the
/// un-fakeable "a valid .dsk inserts" assertion is driven through the server's exact dispatch chain —
/// <see cref="FrameCodec.TryDecodeUpload"/> -> <see cref="UploadValidator.Validate"/> -> the four-arg
/// <c>Apple2Surface.InsertDisk</c> (the PR-Q insert delegate) -> <see cref="FrameCodec.EncodeUploadAck"/> —
/// at the public seam (no env var, no booted demo branch), and the WS legs only assert the receive loop
/// stays healthy on the new binary path (board-agnostic, no seeded asset).</summary>
[Trait("Category", "UAT")]
public class DiskUploadEndpointTests : IClassFixture<WebApplicationFactory<WebProgram>>
{
    private readonly WebApplicationFactory<WebProgram> _factory;
    public DiskUploadEndpointTests(WebApplicationFactory<WebProgram> factory) => _factory = factory;

    private static byte[] DkFrame(byte drive, byte format, byte[] body)
    {
        var f = new byte[5 + body.Length];
        f[0] = (byte)'D'; f[1] = (byte)'K'; f[2] = 0x01; f[3] = drive; f[4] = format;
        body.CopyTo(f.AsSpan(5));
        return f;
    }

    // The server's dispatch chain, exercised at the public seam (mirrors DemoSession.DispatchUpload): decode
    // the reassembled DK frame, re-validate, on success load via the four-arg PR-Q insert delegate, then
    // encode the ack the client decodes. Returns the decoded ack's upload object.
    private static JsonElement DispatchAndDecodeAck(byte[] frame,
        Action<int, byte[], DiskFormat, string>? insertDisk)
    {
        byte[] ack;
        if (!FrameCodec.TryDecodeUpload(frame, out UploadFrame upload))
        {
            ack = FrameCodec.EncodeUploadAck(0, new UploadResult(false, "That image looks corrupt"));
        }
        else
        {
            UploadResult result = UploadValidator.Validate(upload);
            if (!result.Ok || insertDisk is null)
            {
                ack = FrameCodec.EncodeUploadAck(upload.Drive, result.Ok
                    ? new UploadResult(false, "That image looks corrupt")
                    : result);
            }
            else
            {
                insertDisk(upload.Drive, upload.Bytes, upload.Format, $"upload ({upload.Format})".ToLowerInvariant());
                ack = FrameCodec.EncodeUploadAck(upload.Drive, new UploadResult(true, ""));
            }
        }

        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(ack)["ST ".Length..]);
        return doc.RootElement.GetProperty("upload").Clone();
    }

    [Fact]
    public void A_valid_dsk_DK_frame_inserts_into_drive_N()
    {
        // The un-fakeable insert leg: a real Apple2Surface's four-arg InsertDisk is the dispatch delegate, so
        // a passing ok:true means a valid .dsk DK frame actually loaded into the running Disk II — and a
        // running read pulls a nibble off it. Driven through the public seam, not the process env var.
        var sys = new byte[0x3000];
        sys[0x2FFC] = 0x62; sys[0x2FFD] = 0xFA;
        Apple2Surface surface = Apple2Surface.Create(
            sys, diskBootRom: null, charRom: null, frameSink: _ => { }, audioSink: _ => { });
        Action<int, byte[], DiskFormat, string> insertDisk =
            (drive, bytes, format, label) => surface.InsertDisk(drive, bytes, format, label);

        byte[] dsk = new byte[35 * 16 * 256];                 // exactly 143,360
        for (int i = 0; i < dsk.Length; i++) dsk[i] = (byte)((i + 1) & 0xFF);
        JsonElement ack = DispatchAndDecodeAck(DkFrame(1, 1, dsk), insertDisk);

        Assert.Equal(1, ack.GetProperty("drive").GetInt32());
        Assert.True(ack.GetProperty("ok").GetBoolean());

        // The loaded disk is real: a running read pulls a nibble (the same proof R's gate uses).
        var bus = surface.Machine.Space(CpuEmulator.Core.AddressSpaceKind.Program);
        bus.Read8(0xC0E9);   // motor on
        bool sawNibble = false;
        for (int i = 0; i < 50_000 && !sawNibble; i++)
            if ((bus.Read8(0xC0EC) & 0x80) != 0) sawNibble = true;
        Assert.True(sawNibble, "a valid uploaded .dsk must read back a nibble");
    }

    [Fact]
    public void A_wrong_length_dsk_DK_frame_is_rejected()
    {
        // The bad-length leg: a 100-byte .dsk DK frame re-validates to corrupt and produces an error ack —
        // and InsertDisk is never called (the validator rejects before dispatch loads anything).
        bool inserted = false;
        Action<int, byte[], DiskFormat, string> insertDisk = (_, __, ___, ____) => inserted = true;

        JsonElement ack = DispatchAndDecodeAck(DkFrame(1, 1, new byte[100]), insertDisk);

        Assert.False(ack.GetProperty("ok").GetBoolean());
        Assert.Equal("That image looks corrupt", ack.GetProperty("message").GetString());
        Assert.False(inserted, "a bad-length image must never reach InsertDisk");
    }

    [Fact]
    public async Task A_key_event_still_works_after_the_binary_path_is_added()
    {
        // The single-text-frame (key) protocol is unaffected: send a key, confirm the session keeps
        // streaming FB frames (the binary branch did not break the text path). Board-agnostic — any booted
        // branch (demo / Spectrum / Apple) streams FB — so no seeded asset and no env var are needed.
        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
        using WebSocket ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        string key = "{\"action\":\"down\",\"code\":\"KeyA\",\"char\":\"A\"}";
        await ws.SendAsync(Encoding.UTF8.GetBytes(key), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[8 + 560 * 216 * 4];
        bool sawFb = false;
        for (int i = 0; i < 200 && !sawFb; i++)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Binary && buffer[0] == (byte)'F' && buffer[1] == (byte)'B')
                sawFb = true;
        }
        Assert.True(sawFb, "the key/text path must stay healthy after the DK binary branch lands");
    }

    [Fact]
    public async Task A_binary_DK_frame_keeps_the_live_session_streaming()
    {
        // The un-fakeable session-health leg for the NEW binary path: a binary DK frame goes through the
        // (new) reassemble + dispatch branch of the receive loop without crashing it, and the session keeps
        // streaming FB frames. Board-agnostic (no env var, no seeded asset) — the demo branch's insertDisk is
        // null so the dispatch produces an error ack, but the invariant under test is "the new binary branch
        // does not break the receive loop / FB stream".
        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
        using WebSocket ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        byte[] dsk = new byte[35 * 16 * 256];                 // a well-formed-length .dsk DK frame
        await ws.SendAsync(DkFrame(1, 1, dsk), WebSocketMessageType.Binary, true, CancellationToken.None);

        var buffer = new byte[8 + 560 * 216 * 4];
        bool sawFb = false;
        for (int i = 0; i < 300 && !sawFb; i++)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Binary && buffer[0] == (byte)'F' && buffer[1] == (byte)'B')
                sawFb = true;
        }
        Assert.True(sawFb, "the session must keep streaming after a binary DK upload frame");
    }
}
