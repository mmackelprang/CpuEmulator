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

/// <summary>One WebSocket session: a machine surface whose frames stream to the socket, a
/// wall-clock pump task, and an inbound key-event read loop. Boots the ZX Spectrum when its ROM is
/// cached, else the SP0 demo board. Closes when the socket closes or the request aborts.</summary>
internal static class DemoSession
{
    // ~60 Hz pacing for the demo: one ~16,667-cycle slice every ~16 ms of wall-clock.
    private const long SliceCycles = 16_667;
    private static readonly TimeSpan SlicePeriod = TimeSpan.FromMilliseconds(16);

    // The Spectrum runs at 3.5 MHz: one ~70k-T slice every ~20 ms wall-clock (50 Hz).
    private const long SpectrumPumpCycles = 69_888;
    private static readonly TimeSpan SpectrumPeriod = TimeSpan.FromMilliseconds(20);

    // The Apple ][+ runs at ~1.0205 MHz: a ~17,030-cycle slice every ~16 ms (60 Hz, matches Apple2Video).
    private const long AppleSliceCycles = 17_030;
    private static readonly TimeSpan ApplePeriod = TimeSpan.FromMilliseconds(16);

    public static async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        Channel<byte[]> frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });
        Channel<byte[]> audio = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

        // Boot the SoftCard (CP/M) when BOTH the Apple system ROM and the CP/M .dsk are cached; else fall
        // back to the Apple ][+ when its system ROM is cached; else the Spectrum, else the demo. (Each
        // subsequent asset is only probed when the earlier branch isn't taken — no file-stat in the common
        // boot path.)
        string? appleRom = CpuEmulator.Machines.Apple2Rom.TryGetPath();
        string? cpmDisk = CpuEmulator.Machines.SoftCardCpm.TryGetDiskPath();
        ISurfacePump pump;
        string assetState;   // surfaced to the client banner / status line
        if (appleRom is not null && cpmDisk is not null)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom);
            byte[] bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom()
                ?? throw new InvalidOperationException(
                    "SoftCard CP/M needs the slot-6 Disk II boot ROM (disk2.rom) — run tools/get-apple2-roms.");
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            CpuEmulator.Core.IBlockDevice cpm = CpuEmulator.Machines.SoftCardCpm.LoadBlockDevice(cpmDisk);
            SoftCardSurface softcard = SoftCardSurface.Create(sys, bootRom, charRom, cpm,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            pump = new SurfacePump(softcard.Host, AppleSliceCycles, ApplePeriod);
            assetState = "softcard-cpm";
        }
        else if (appleRom is not null)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom);
            byte[]? bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom();
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            Apple2Surface apple = Apple2Surface.Create(sys, bootRom, charRom,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            pump = new SurfacePump(apple.Host, AppleSliceCycles, ApplePeriod);
            assetState = charRom is null ? "apple-fallback-font" : "apple";
        }
        else if (CpuEmulator.Machines.SpectrumRom.TryGetPath() is { } romPath)
        {
            byte[] rom = CpuEmulator.Machines.SpectrumRom.Load(romPath);
            SpectrumSurface spectrum = SpectrumSurface.Create(rom,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            pump = new SurfacePump(spectrum.Host, SpectrumPumpCycles, SpectrumPeriod);
            assetState = "spectrum";
        }
        else
        {
            // The demo board has no audio device; the audio channel stays empty.
            DemoBoardSurface demo = DemoBoardSurface.Create(f => frames.Writer.TryWrite(f));
            pump = new SurfacePump(demo.Host, SliceCycles, SlicePeriod);
            assetState = "demo";
        }

        // One-shot board/asset string the client renders into the banner + status line (the design
        // copy.md strings). Text frame — the binary FB/AU path below is untouched (PR-H seam; the richer
        // ST status frame is PR-P).
        if (socket.State == WebSocketState.Open)
            await socket.SendAsync(Encoding.UTF8.GetBytes($"ST {assetState}"),
                WebSocketMessageType.Text, endOfMessage: true, ct);

        Task drive = pump.RunAsync(ct);
        Task sendFrames = SendBinaryAsync(socket, frames.Reader, ct);
        Task sendAudio = SendBinaryAsync(socket, audio.Reader, ct);
        Task recv = ReceiveKeysAsync(socket, pump, ct);

        await Task.WhenAny(drive, sendFrames, sendAudio, recv);
        frames.Writer.TryComplete();
        audio.Writer.TryComplete();
        try { await Task.WhenAll(drive, sendFrames, sendAudio, recv); } catch { /* teardown races expected */ }
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

    private static async Task ReceiveKeysAsync(WebSocket socket, ISurfacePump pump,
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
                pump.PostKey(e);
        }
    }

    /// <summary>A surface the session can step on a wall-clock timer and route keys to — both the
    /// SP0 demo and the Spectrum expose a <see cref="MachineHost"/>, so this hides which is running.</summary>
    private interface ISurfacePump
    {
        Task RunAsync(CancellationToken ct);
        void PostKey(in KeyEvent e);
    }

    private sealed class SurfacePump : ISurfacePump
    {
        private readonly MachineHost _host;
        private readonly long _slice;
        private readonly TimeSpan _period;
        public SurfacePump(MachineHost host, long slice, TimeSpan period)
        {
            _host = host;
            _slice = slice;
            _period = period;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(_period);
            while (await timer.WaitForNextTickAsync(ct))
                _host.Step(_slice);
        }

        public void PostKey(in KeyEvent e) => _host.PostKey(e);
    }
}
