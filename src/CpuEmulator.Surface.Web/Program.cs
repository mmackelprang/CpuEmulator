using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using CpuEmulator.Core;

namespace CpuEmulator.Surface.Web;

// NOTE: the plan's literal Program.cs uses top-level statements + a `public partial class Program`
// marker. That synthesizes a Program type in the GLOBAL namespace, which collides with
// CpuEmulator.SpecImporter's own public global `Program` in the test project (the tests reference
// both executables). To keep WebApplicationFactory<Program> working without breaking the existing
// SpecImporter tests, the entry point is an explicit `public class Program` scoped to
// CpuEmulator.Surface.Web — same wiring, just an unambiguous, namespaced Program type.
public class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        WebApplication app = builder.Build();

        app.UseDefaultFiles();   // serve wwwroot/index.html at "/"
        app.UseStaticFiles();
        app.UseWebSockets();

        // One machine per connected client (single-machine-per-host; a new socket = a fresh demo).
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            await DemoSession.RunAsync(socket, context.RequestAborted);
        });

        app.Run();
    }
}

/// <summary>One WebSocket session: a DemoBoardSurface whose frames stream to the socket, a
/// wall-clock pump task, and an inbound key-event read loop. Closes when the socket closes or
/// the request aborts.</summary>
internal static class DemoSession
{
    // ~60 Hz pacing: one ~16,667-cycle slice every ~16 ms of wall-clock.
    private const long SliceCycles = 16_667;
    private static readonly TimeSpan SlicePeriod = TimeSpan.FromMilliseconds(16);

    public static async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        Channel<byte[]> frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });
        Channel<byte[]> audio = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

        DemoBoardSurface surface = DemoBoardSurface.Create(frame => frames.Writer.TryWrite(frame));
        // The demo board has no audio device; the audio channel stays empty. A machine surface that
        // wires an IAudioSink (the Spectrum, Phase 2) writes here via its MachineHost audio sink.

        Task pump = PumpAsync(surface, ct);
        Task sendFrames = SendBinaryAsync(socket, frames.Reader, ct);
        Task sendAudio = SendBinaryAsync(socket, audio.Reader, ct);
        Task recv = ReceiveKeysAsync(socket, surface, ct);

        await Task.WhenAny(pump, sendFrames, sendAudio, recv);
        frames.Writer.TryComplete();
        audio.Writer.TryComplete();
        try { await Task.WhenAll(pump, sendFrames, sendAudio, recv); } catch { /* teardown races expected */ }
    }

    private static async Task PumpAsync(DemoBoardSurface surface, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SlicePeriod);
        while (await timer.WaitForNextTickAsync(ct))
            surface.Host.Step(SliceCycles);
    }

    private static async Task SendBinaryAsync(WebSocket socket, ChannelReader<byte[]> reader,
                                              CancellationToken ct)
    {
        await foreach (byte[] frame in reader.ReadAllAsync(ct))
        {
            if (socket.State != WebSocketState.Open)
                break;
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
    }

    private static async Task ReceiveKeysAsync(WebSocket socket, DemoBoardSurface surface,
                                              CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                break;
            }
            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (FrameCodec.TryDecodeKey(json, out KeyEvent e))
                surface.Host.PostKey(e);
        }
    }
}
