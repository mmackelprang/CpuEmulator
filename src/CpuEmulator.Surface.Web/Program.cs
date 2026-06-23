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
        // The `--system <name>` override (deterministic launcher seam): scan a COPY of args BEFORE building the
        // WebApplication. `--system list` prints the valid names and exits; a valid name forces the boot branch
        // (DemoSession.ForcedSystem); an unknown name errors out. The ORIGINAL args pass through to
        // CreateBuilder unchanged — ASP.NET treats an unknown `--key value` pair as inert config, so the
        // auto-probe path (no `--system`) stays byte-for-byte identical.
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--system", StringComparison.Ordinal))
                continue;
            string name = args[i + 1];
            if (string.Equals(name, "list", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Valid --system names: " + string.Join(", ", DemoSession.SystemNames));
                return;
            }
            if (DemoSession.TryParseSystem(name, out DemoSession.WebSystem forced))
            {
                DemoSession.ForcedSystem = forced;
            }
            else
            {
                Console.Error.WriteLine($"Unknown --system '{name}'. Valid names: "
                    + string.Join(", ", DemoSession.SystemNames));
                Environment.Exit(2);
            }
            break;
        }

        // Default the content root to the app's OWN directory (where wwwroot is published), not the
        // process CWD. Without this, launching the published DLL from anywhere but the project dir leaves
        // ASP.NET hunting for wwwroot under the CWD → "WebRootPath not found" → a 404 at "/". An explicit
        // --contentRoot on the command line still wins (CreateBuilder applies args AFTER these options).
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
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

    /// <summary>Which system the web server boots, in precedence order. The asset-probe order in
    /// <see cref="RunAsync"/> mirrors this; <see cref="SelectSystem"/> is the pure decision.</summary>
    internal enum WebSystem { Apl2Cpm3Videx, SoftCardCpm22, Apple2, Pascal, Spectrum, Demo }

    /// <summary>A <c>--system &lt;name&gt;</c> override parsed in <see cref="Program.Main"/>: when non-null it
    /// forces the boot branch in <see cref="RunAsync"/> regardless of asset precedence (the asset probes still
    /// run so the forced branch has the inputs it needs). Null = the normal auto-probe (byte-for-byte
    /// unchanged).</summary>
    internal static WebSystem? ForcedSystem;

    /// <summary>The friendly <c>--system</c> names, in the order they map to <see cref="WebSystem"/>. Surfaced
    /// by <c>--system list</c> and the unknown-name error.</summary>
    internal static readonly string[] SystemNames = { "cpm3", "cpm22", "apple2", "pascal", "spectrum", "demo" };

    /// <summary>Map a friendly <c>--system</c> name (case-insensitive) to the <see cref="WebSystem"/> branch.
    /// Returns false for an unknown name. <c>cpm3</c>→Apl2Cpm3Videx, <c>cpm22</c>→SoftCardCpm22,
    /// <c>apple2</c>→Apple2, <c>pascal</c>→Pascal, <c>spectrum</c>→Spectrum, <c>demo</c>→Demo.</summary>
    internal static bool TryParseSystem(string name, out WebSystem system)
    {
        switch (name.ToLowerInvariant())
        {
            case "cpm3": system = WebSystem.Apl2Cpm3Videx; return true;
            case "cpm22": system = WebSystem.SoftCardCpm22; return true;
            case "apple2": system = WebSystem.Apple2; return true;
            case "pascal": system = WebSystem.Pascal; return true;
            case "spectrum": system = WebSystem.Spectrum; return true;
            case "demo": system = WebSystem.Demo; return true;
            default: system = WebSystem.Demo; return false;
        }
    }

    /// <summary>The pure precedence decision (no I/O) — the single source of truth for which system the web
    /// server boots, given which assets are present. apl2cpm3 (80-col CP/M 3.1) takes priority over the 2.2
    /// (40-col) disk when its assets are present; 2.2 stays the fallback. Mirrors the asset-probe order in
    /// <see cref="RunAsync"/>.</summary>
    /// <param name="videxFirmware">Whether the REAL Videx $C800 firmware is cached. It gates the apl2cpm3
    /// branch because the apl2cpm3 CRT80 console JMPs into that firmware window to paint the 80-col VRAM —
    /// without the real firmware the 80-col screen renders NOTHING (see Apl2Cpm3Vectors.TryGetVidexAssets /
    /// the Apl2Cpm3VidexFact doc). Requiring it avoids selecting a blank-screen apl2cpm3 boot; absent it we
    /// correctly fall through to the 2.2 (40-col) branch.</param>
    /// <param name="pascalDisks">Whether BOTH Apple Pascal disks (APPLE1 boot + APPLE0 program) are cached.
    /// Selects the Pascal branch (Apple ROM + the two Pascal disks → Pascal). Placed AFTER the CP/M branches
    /// because it needs the Pascal disks the CP/M rigs don't stage; placed BEFORE the bare Apple ][+ because
    /// Pascal needs MORE assets than the bare ][+ (which boots on the system ROM alone).</param>
    internal static WebSystem SelectSystem(bool appleRom, bool apl2cpm3Disk, bool videxFirmware,
                                           bool cpm22Disk, bool pascalDisks, bool spectrumRom)
    {
        if (appleRom && apl2cpm3Disk && videxFirmware) return WebSystem.Apl2Cpm3Videx;
        if (appleRom && cpm22Disk) return WebSystem.SoftCardCpm22;
        if (appleRom && pascalDisks) return WebSystem.Pascal;
        if (appleRom) return WebSystem.Apple2;
        if (spectrumRom) return WebSystem.Spectrum;
        return WebSystem.Demo;
    }

    /// <summary>The HUD's <c>board</c> row name for the PF frame. Reuses the Apple status provider's live
    /// board string when present (so the HUD matches the ST snapshot); otherwise a per-system descriptor for
    /// the boards that ride the legacy one-shot ST text frame (Spectrum/demo).</summary>
    private static string PerfBoardName(WebSystem system, Func<MachineStatus>? statusProvider)
    {
        if (statusProvider is not null)
            return statusProvider().Board;   // the Apple surfaces' live board name
        return system switch
        {
            WebSystem.Spectrum => "ZX Spectrum 48K",
            _ => "demo board",
        };
    }

    public static async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        Channel<byte[]> frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });
        Channel<byte[]> audio = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

        // Boot order (precedence in SelectSystem): the apl2cpm3 80-col CP/M 3.1 SoftCard+Videx when its
        // assets (Apple ROM + apl2cpm3 Disk 1 + REAL Videx firmware) are cached; else the 2.2 40-col SoftCard
        // (Apple ROM + the 2.2 .dsk); else the Apple ][+ (system ROM only); else the Spectrum; else the demo.
        // Assets are probed lazily/short-circuit: the CP/M discs + Videx firmware are only stat'd when the
        // Apple system ROM is present (a missing Apple ROM means no SoftCard boot regardless), so the
        // Spectrum/demo paths take no Apple-asset file-stat.
        string? appleRom = CpuEmulator.Machines.Apple2Rom.TryGetPath();
        string? apl2cpm3DiskPath = null;
        byte[]? videxFirmware = null;   // the REAL $C800 firmware (load-bearing for the apl2cpm3 80-col console)
        string? cpmDisk = null;         // the 2.2 40-col .dsk
        string? pascalBootPath = null;      // APPLE1 (drive 1): SYSTEM.APPLE + SYSTEM.PASCAL
        string? pascalProgramPath = null;   // APPLE0 (drive 2): the compiler/editor set
        if (appleRom is not null)
        {
            apl2cpm3DiskPath = CpuEmulator.Machines.Apl2Cpm3.TryGetBootDiskPath();
            videxFirmware = CpuEmulator.Machines.VidexRom.TryLoadFirmware();   // non-null == the real firmware is cached
            cpmDisk = CpuEmulator.Machines.SoftCardCpm.TryGetDiskPath();
            pascalBootPath = CpuEmulator.Machines.Pascal.TryGetBootDiskPath();
            pascalProgramPath = CpuEmulator.Machines.Pascal.TryGetProgramDiskPath();
        }

        // Hoist the Spectrum probe (like appleRom) so the branch below reuses the already-stat'd value
        // instead of re-probing — a naked TryGetPath()! on a fresh stat would null-deref if the cache file
        // vanished between the SelectSystem probe and the branch (an owner-managed cache; the tooling can
        // delete it). The Apple disk probes are skipped when there is no Apple ROM, so this stat always runs.
        string? spectrumRomPath = CpuEmulator.Machines.SpectrumRom.TryGetPath();

        // The `--system <name>` override (parsed in Main) forces the branch; otherwise the pure auto-probe
        // decision. The probes above run REGARDLESS — a forced branch still needs its assets loaded below, and
        // omitting `--system` keeps the selection byte-for-byte unchanged.
        WebSystem system = DemoSession.ForcedSystem ?? SelectSystem(
            appleRom: appleRom is not null,
            apl2cpm3Disk: apl2cpm3DiskPath is not null,
            videxFirmware: videxFirmware is not null,
            cpm22Disk: cpmDisk is not null,
            pascalDisks: pascalBootPath is not null && pascalProgramPath is not null,
            spectrumRom: spectrumRomPath is not null);

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
        if (system == WebSystem.Apl2Cpm3Videx)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom!);
            byte[] bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom()
                ?? throw new InvalidOperationException(
                    "apl2cpm3 CP/M 3.1 needs the slot-6 Disk II boot ROM (disk2.rom) — run tools/get-apple2-roms.");
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            byte[]? videxChar = CpuEmulator.Machines.VidexRom.TryLoadCharRom();        // optional (synthetic fallback)
            byte[]? videxFw = videxFirmware;                                           // already probed (REAL firmware)
            CpuEmulator.Core.IBlockDevice cpm3 = CpuEmulator.Machines.Apl2Cpm3.LoadBootDisk(apl2cpm3DiskPath!);
            SoftCardVidexSurface softcard = SoftCardVidexSurface.CreateApl2Cpm3(sys, bootRom, charRom,
                videxChar, videxFw, cpm3,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            host = softcard.Host; slice = AppleSliceCycles; period = ApplePeriod;
            assetState = "apl2cpm3-cpm3-videx";
            string asset = assetState;                                     // capture for the provider
            statusProvider = () => softcard.Status() with { Asset = asset };
            insertDisk = (drive, bytes, format, label) => softcard.InsertDisk(drive, bytes, format, label);
            ejectDisk = drive => softcard.EjectDisk(drive);
        }
        else if (system == WebSystem.SoftCardCpm22)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom!);
            byte[] bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom()
                ?? throw new InvalidOperationException(
                    "SoftCard CP/M needs the slot-6 Disk II boot ROM (disk2.rom) — run tools/get-apple2-roms.");
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            byte[]? videxChar = CpuEmulator.Machines.VidexRom.TryLoadCharRom();      // optional (synthetic fallback)
            byte[]? videxFw = videxFirmware;                                         // optional (already probed)
            CpuEmulator.Core.IBlockDevice cpm = CpuEmulator.Machines.SoftCardCpm.LoadBlockDevice(cpmDisk!);
            SoftCardVidexSurface softcard = SoftCardVidexSurface.Create(sys, bootRom, charRom,
                videxChar, videxFw, cpm,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            host = softcard.Host; slice = AppleSliceCycles; period = ApplePeriod;
            assetState = "softcard-cpm-videx";
            string asset = assetState;                                     // capture for the provider
            statusProvider = () => softcard.Status() with { Asset = asset };
            insertDisk = (drive, bytes, format, label) => softcard.InsertDisk(drive, bytes, format, label);
            ejectDisk = drive => softcard.EjectDisk(drive);
        }
        else if (system == WebSystem.Apple2)
        {
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom!);
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
        else if (system == WebSystem.Pascal)
        {
            // Apple Pascal (UCSD p-System) on the PR #153 board (reused via Pascal.CreateBoard inside
            // CreatePascal). Each input guards its own null with a clear message so a forced `--system pascal`
            // with missing assets fails loudly (and deterministically) rather than NRE-ing — the launcher
            // script stages the disks first, but a bare override should still say what's missing.
            byte[] sys = CpuEmulator.Machines.Apple2Rom.Load(appleRom
                ?? throw new InvalidOperationException("--system pascal needs the Apple ][+ system ROM (apple2plus.rom) — run tools/get-apple2-roms."));
            byte[] bootRom = CpuEmulator.Machines.Apple2Rom.TryLoadDiskRom()
                ?? throw new InvalidOperationException("Apple Pascal needs the slot-6 Disk II boot ROM (disk2.rom) — run tools/get-apple2-roms.");
            byte[]? charRom = CpuEmulator.Machines.Apple2Rom.TryLoadCharRom();
            string bootDisk = pascalBootPath
                ?? throw new InvalidOperationException("Apple Pascal needs APPLE1.dsk (boot) — run tools/get-apple-pascal.");
            string? programDisk = pascalProgramPath;   // APPLE0 (drive 2); the authentic two-drive boot needs it
            Apple2Surface pascal = Apple2Surface.CreatePascal(sys, bootRom, charRom, bootDisk, programDisk,
                f => frames.Writer.TryWrite(f), a => audio.Writer.TryWrite(a));
            host = pascal.Host; slice = AppleSliceCycles; period = ApplePeriod;
            assetState = "apple-pascal";
            string asset = assetState;                                     // capture for the provider
            statusProvider = () => pascal.Status() with { Asset = asset };
            insertDisk = (drive, bytes, format, label) => pascal.InsertDisk(drive, bytes, format, label);
            ejectDisk = drive => pascal.EjectDisk(drive);
        }
        else if (system == WebSystem.Spectrum)
        {
            // Reuse the hoisted probe (only reached with no Apple ROM and the Spectrum ROM present) — non-null
            // by SelectSystem; no fresh stat, so no TOCTOU null-deref.
            string romPath = spectrumRomPath!;
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

        // The PF perf/telemetry frame (perf-overlay HUD): pushed unconditionally at ~3 Hz through the SAME
        // text channel the ST frame uses (the client routes by the "PF "/"ST " prefix). Built for EVERY
        // system — the HUD works on the Spectrum/demo too. The board name is the Apple status provider's
        // when present, else a per-system descriptor (the HUD's `board` row). Separate from the ST pusher:
        // the perf frame never touches the drive/mode UI's ST dedupe.
        string perfBoardName = PerfBoardName(system, statusProvider);
        var perfPusher = new PerfPusher(host.Machine, () => perfBoardName,
            f => statusFrames.Writer.TryWrite(f));

        // The pump is built once, post-branch, with the optional status pusher (null for Spectrum/demo —
        // their tick stays byte-for-byte unchanged) and the always-on perf pusher.
        ISurfacePump pump = new SurfacePump(host, slice, period, statusPusher, perfPusher);

        Task drive = pump.RunAsync(ct);
        Task sendFrames = SendBinaryAsync(socket, frames.Reader, ct);
        Task sendAudio = SendBinaryAsync(socket, audio.Reader, ct);
        Task sendStatus = SendTextAsync(socket, statusFrames.Reader, ct);
        Action<byte[]> pushText = f => statusFrames.Writer.TryWrite(f);
        Task recv = ReceiveKeysAsync(socket, pump, insertDisk, ejectDisk, pushText, ct);

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
                                              Action<byte[]> pushText,
                                              CancellationToken ct)
    {
        const int MaxUploadBytes = 2 * 1024 * 1024;   // the design's 2 MB cap, re-enforced server-side
        var buffer = new byte[8192];
        var binaryAccumulator = new MemoryStream();
        // Once a single binary message blows the cap, every remaining fragment of THAT message is drained
        // (not re-accumulated) so a too-large upload can't dispatch a partial tail frame; the "too large"
        // ack is sent once, at the message's EndOfMessage. Reset for the next message.
        bool capExceeded = false;
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Drain the rest of an already-over-cap message: ignore its fragments, ack once at the end.
                if (capExceeded)
                {
                    if (result.EndOfMessage)
                    {
                        capExceeded = false;
                        pushText(FrameCodec.EncodeUploadAck(0,
                            new UploadResult(false, "File too large — Disk II images are under ~250 KB")));
                    }
                    continue;
                }

                // Accumulate fragments until the whole DK message is in (a .dsk is 143,360 bytes — many
                // 8 KiB fragments). Cap the accumulation to refuse an oversized upload without OOM.
                if (binaryAccumulator.Length + result.Count > MaxUploadBytes)
                {
                    binaryAccumulator.SetLength(0);
                    if (result.EndOfMessage)
                        pushText(FrameCodec.EncodeUploadAck(0,
                            new UploadResult(false, "File too large — Disk II images are under ~250 KB")));
                    else
                        capExceeded = true;   // drain the remaining fragments of this oversized message
                    continue;
                }
                binaryAccumulator.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                byte[] frame = binaryAccumulator.ToArray();
                binaryAccumulator.SetLength(0);
                DispatchUpload(frame, insertDisk, pushText);
                continue;
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
                    // A vanished/truncated/unreadable/malformed library file is a normal user condition (the
                    // cache is owner-managed), NOT a server fault: ReadAllBytes (TOCTOU after TryResolve), the
                    // DiskImage ctor (a non-256-multiple .dsk/.po length), and WozFluxImage (a malformed .woz —
                    // throws InvalidDataException) can throw. Swallow so a bad disk never tears down the live WS
                    // session — the insert simply doesn't happen. (All three formats — incl. .woz — load here.)
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        insertDisk(cmd.Drive, bytes, format, Path.GetFileNameWithoutExtension(path));
                    }
                    catch (Exception ex) when (ex is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException
                                               or NotSupportedException
                                               or InvalidDataException)
                    {
                        // Intentionally ignored — the live session keeps streaming; the drive is unchanged.
                    }
                }
            }
            else if (FrameCodec.TryDecodeKey(json, out KeyEvent e))
            {
                pump.PostKey(e);
            }
        }

        binaryAccumulator.Dispose();
    }

    /// <summary>Decode + validate a reassembled DK upload frame and, on success, load it into the running
    /// drive (the shipped PR-Q insert via the hoisted delegate). Always pushes an upload-result ack so the
    /// client's UPLOADING state resolves to INSERTED or the inline error.</summary>
    private static void DispatchUpload(byte[] frame, Action<int, byte[], DiskFormat, string>? insertDisk,
                                       Action<byte[]> pushText)
    {
        if (!FrameCodec.TryDecodeUpload(frame, out UploadFrame upload))
        {
            pushText(FrameCodec.EncodeUploadAck(0, new UploadResult(false, "That image looks corrupt")));
            return;
        }

        UploadResult result = UploadValidator.Validate(upload);
        if (!result.Ok || insertDisk is null)
        {
            // A validation failure forwards the validator's calm reason; a valid image on a session with no
            // Apple disk drive (Spectrum/demo) is honestly "not supported here" — NOT "corrupt" (the file is
            // fine; this surface just can't take it). The Apple branches always wire insertDisk, so the
            // null case is only reachable from a non-Apple session / a crafted request.
            pushText(FrameCodec.EncodeUploadAck(upload.Drive, result.Ok
                ? new UploadResult(false, "Disk upload isn't supported in this session")
                : result));
            return;
        }

        // All three formats (.dsk/.po via Q, .woz via WozFluxImage) load here; a malformed image throws below.
        // M5 note: this synthetic "upload (dsk|po|woz)" label is the only name the host knows for an uploaded
        // image — the surface shows the real filename optimistically (client-captured). The proper fix
        // (carry the original filename through the DK/ST frame) is a follow-on per copy.md §6.2.
        try
        {
            insertDisk(upload.Drive, upload.Bytes, upload.Format, $"upload ({upload.Format})".ToLowerInvariant());
            pushText(FrameCodec.EncodeUploadAck(upload.Drive, new UploadResult(true, "")));
        }
        catch (Exception)
        {
            // DiskImageFactory/DiskImage throws on a malformed image the length-check let through.
            pushText(FrameCodec.EncodeUploadAck(upload.Drive, new UploadResult(false, "That image looks corrupt")));
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
        // The PF perf frame cadence (~3 Hz / 333 ms): smooth enough that rates don't visibly jitter, slow
        // enough to be negligible load and NOT per-frame (handoff §6.3). Decoupled from the frame _period
        // (~16-20 ms) by accumulating wall-time across pump ticks.
        private static readonly TimeSpan PerfPeriod = TimeSpan.FromMilliseconds(333);

        private readonly MachineHost _host;
        private readonly long _slice;
        private readonly TimeSpan _period;
        private readonly StatusPusher? _statusPusher;
        private readonly PerfPusher? _perfPusher;
        public SurfacePump(MachineHost host, long slice, TimeSpan period, StatusPusher? statusPusher = null,
                           PerfPusher? perfPusher = null)
        {
            _host = host;
            _slice = slice;
            _period = period;
            _statusPusher = statusPusher;
            _perfPusher = perfPusher;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(_period);
            var perfClock = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan nextPerf = TimeSpan.Zero;   // push the first PF frame on the first tick (HUD primes fast)
            while (await timer.WaitForNextTickAsync(ct))
            {
                _host.Step(_slice);
                _statusPusher?.Tick();        // push ST only when the real snapshot changed
                // Push the PF perf frame on its own ~3 Hz beat (unconditional — no on-change gate), throttled
                // independently of the ~60 Hz frame pump so the telemetry stays cheap.
                if (_perfPusher is not null && perfClock.Elapsed >= nextPerf)
                {
                    _perfPusher.Tick();
                    nextPerf = perfClock.Elapsed + PerfPeriod;
                }
            }
        }

        public void PostKey(in KeyEvent e) => _host.PostKey(e);
    }
}
