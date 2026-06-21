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

        // The disk-library catalog (design D11 / T-C): the per-drive [ Library ▾] select fetches this.
        // A pure file-system read of the cache (no machine state) — served as compact JSON.
        app.MapGet("/disks", () =>
        {
            var entries = CpuEmulator.Machines.DiskCatalog.List()
                .Select(e => new { id = e.Id, name = e.Name, format = e.Format, cpm = e.Cpm, supported = e.Supported })
                .ToArray();
            return Results.Json(entries);
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
        // Only stat the CP/M .dsk when the Apple system ROM is present — the SoftCard needs both, so a
        // missing Apple ROM means no SoftCard boot regardless, and the Spectrum/demo paths take no file-stat.
        string? cpmDisk = appleRom is not null ? CpuEmulator.Machines.SoftCardCpm.TryGetDiskPath() : null;
        // Hoisted per-branch state: each branch sets the host + slice/period (the pump is built ONCE after
        // the branch, with the optional status pusher) and either the Apple status provider (the live ST
        // frame) or leaves it null (Spectrum/demo keep the legacy one-shot "ST <assetState>" text frame).
        MachineHost host;
        long slice;
        TimeSpan period;
        Func<MachineStatus>? statusProvider = null;
        Action<int, byte[], DiskFormat, string>? insertDisk = null;   // library/upload insert (R/S)
        Action<int>? ejectDisk = null;                                // library eject (R)
        string assetState;   // surfaced to the client banner / status line
        if (appleRom is not null && cpmDisk is not null)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom);
            byte[] bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom()
                ?? throw new InvalidOperationException(
                    "SoftCard CP/M needs the slot-6 Disk II boot ROM (disk2.rom) — run tools/get-apple2-roms.");
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            byte[]? videxChar = CpuEmulator.Machines.VidexRom.TryLoadCharRom();      // optional (synthetic fallback)
            byte[]? videxFirmware = CpuEmulator.Machines.VidexRom.TryLoadFirmware(); // optional
            CpuEmulator.Core.IBlockDevice cpm = CpuEmulator.Machines.SoftCardCpm.LoadBlockDevice(cpmDisk);
            SoftCardVidexSurface softcard = SoftCardVidexSurface.Create(sys, bootRom, charRom,
                videxChar, videxFirmware, cpm,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            host = softcard.Host; slice = AppleSliceCycles; period = ApplePeriod;
            assetState = "softcard-cpm-videx";
            string asset = assetState;                                     // capture for the provider
            statusProvider = () => softcard.Status() with { Asset = asset };
            insertDisk = (drive, bytes, format, label) => softcard.InsertDisk(drive, bytes, format, label);
            ejectDisk = drive => softcard.EjectDisk(drive);
        }
        else if (appleRom is not null)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom);
            byte[]? bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom();
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            Apple2Surface apple = Apple2Surface.Create(sys, bootRom, charRom,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            host = apple.Host; slice = AppleSliceCycles; period = ApplePeriod;
            assetState = charRom is null ? "apple-fallback-font" : "apple";
            string asset = assetState;                                     // capture for the provider
            statusProvider = () => apple.Status() with { Asset = asset };   // real state, real asset string
            insertDisk = (drive, bytes, format, label) => apple.InsertDisk(drive, bytes, format, label);
            ejectDisk = drive => apple.EjectDisk(drive);
        }
        else if (CpuEmulator.Machines.SpectrumRom.TryGetPath() is { } romPath)
        {
            byte[] rom = CpuEmulator.Machines.SpectrumRom.Load(romPath);
            SpectrumSurface spectrum = SpectrumSurface.Create(rom,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            host = spectrum.Host; slice = SpectrumPumpCycles; period = SpectrumPeriod;
            assetState = "spectrum";   // no Apple status -> the legacy one-shot text frame covers it
        }
        else
        {
            // The demo board has no audio device; the audio channel stays empty.
            DemoBoardSurface demo = DemoBoardSurface.Create(f => frames.Writer.TryWrite(f));
            host = demo.Host; slice = SliceCycles; period = SlicePeriod;
            assetState = "demo";       // no Apple status -> the legacy one-shot text frame covers it
        }

        // The status frame: for the Apple surfaces, a live ST frame pushed on change (design D14, the
        // drive light / mode label / banner consume it). For the Spectrum/demo, the legacy one-shot
        // "ST <assetState>" text frame (no Apple status to reflect) — the client handles both shapes.
        Channel<byte[]> statusFrames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });

        StatusPusher? statusPusher = statusProvider is null
            ? null
            : new StatusPusher(statusProvider, f => statusFrames.Writer.TryWrite(f));

        if (statusPusher is not null)
            statusPusher.Tick();                          // initial real-state push
        else if (socket.State == WebSocketState.Open)     // Spectrum/demo: the legacy one-shot text frame
            await socket.SendAsync(Encoding.UTF8.GetBytes($"ST {assetState}"),
                WebSocketMessageType.Text, endOfMessage: true, ct);

        // The pump is built once, post-branch, with the optional status pusher (null for Spectrum/demo —
        // their tick stays byte-for-byte unchanged).
        ISurfacePump pump = new SurfacePump(host, slice, period, statusPusher);

        Task drive = pump.RunAsync(ct);
        Task sendFrames = SendBinaryAsync(socket, frames.Reader, ct);
        Task sendAudio = SendBinaryAsync(socket, audio.Reader, ct);
        Task sendStatus = SendTextAsync(socket, statusFrames.Reader, ct);
        Task recv = ReceiveKeysAsync(socket, pump, insertDisk, ejectDisk, ct);

        await Task.WhenAny(drive, sendFrames, sendAudio, sendStatus, recv);
        frames.Writer.TryComplete();
        audio.Writer.TryComplete();
        statusFrames.Writer.TryComplete();
        try { await Task.WhenAll(drive, sendFrames, sendAudio, sendStatus, recv); } catch { /* teardown races expected */ }
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

    /// <summary>The text-frame sender: mirrors <see cref="SendBinaryAsync"/> but writes the ST status
    /// frames as WebSocket TEXT (the client's onmessage routes every string to handleStatusText).</summary>
    private static async Task SendTextAsync(WebSocket socket, ChannelReader<byte[]> reader,
                                            CancellationToken ct)
    {
        await foreach (byte[] frame in reader.ReadAllAsync(ct))
        {
            if (socket.State != WebSocketState.Open)
                break;
            await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
    }

    private static async Task ReceiveKeysAsync(WebSocket socket, ISurfacePump pump,
                                              Action<int, byte[], DiskFormat, string>? insertDisk,
                                              Action<int>? ejectDisk,
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
            // Disk-library commands first (their JSON shape is disjoint from a key event); then keys.
            // (TryDecodeKey returns true even for disk JSON — it maps to KeyCode.None — so the disk
            // path must be tried first or a disk-insert/eject would be swallowed as a no-op key.)
            if (FrameCodec.TryDecodeDisk(json, out DiskCommand cmd))
            {
                if (cmd.Eject)
                    ejectDisk?.Invoke(cmd.Drive);
                else if (insertDisk is not null
                         && CpuEmulator.Machines.DiskCatalog.TryResolve(cmd.Id, out string path, out string fmt))
                {
                    DiskFormat format = fmt switch
                    {
                        "po" => DiskFormat.Po,
                        "woz" => DiskFormat.Woz,
                        _ => DiskFormat.Dsk,
                    };
                    // .woz library items are listed-disabled in the UI; never reach here. Guard anyway —
                    // DiskImageFactory.FromBytes throws NotSupportedException for .woz, so skip it.
                    if (format != DiskFormat.Woz)
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        insertDisk(cmd.Drive, bytes, format, Path.GetFileNameWithoutExtension(path));
                    }
                }
            }
            else if (FrameCodec.TryDecodeKey(json, out KeyEvent e))
            {
                pump.PostKey(e);
            }
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
        private readonly StatusPusher? _statusPusher;
        public SurfacePump(MachineHost host, long slice, TimeSpan period, StatusPusher? statusPusher = null)
        {
            _host = host;
            _slice = slice;
            _period = period;
            _statusPusher = statusPusher;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(_period);
            while (await timer.WaitForNextTickAsync(ct))
            {
                _host.Step(_slice);
                _statusPusher?.Tick();        // push ST only when the real snapshot changed
            }
        }

        public void PostKey(in KeyEvent e) => _host.PostKey(e);
    }
}
