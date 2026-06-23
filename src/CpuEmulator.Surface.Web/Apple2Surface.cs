using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>Composes the Apple ][+ for the web surface — the analogue of <see cref="SpectrumSurface"/>.
/// Builds the shared <see cref="Apple2VideoState"/>, the video / keyboard / speaker triad over it, the
/// Language Card, and (optionally) the Disk II controller; assembles the board via
/// <see cref="Apple2Board.SpecWithSystem"/> (with the slot-6 $C600 boot ROM when present), resets it, and
/// wires a <see cref="MachineHost"/> whose DISPLAY = the video chip, KEYBOARD = the keyboard chip, AUDIO =
/// the speaker chip (three objects over one shared state — unlike the Spectrum's single ULA). When the
/// boot ROM is absent the board uses <see cref="Apple2Board.SpecWithDiskII"/> (no $C600 window — no disk
/// boot, but the ROM-monitor `]` still appears). The char ROM is optional (Apple2Font.Fallback covers it).</summary>
public sealed record Apple2Surface(
    Machine Machine, Apple2Video Video, Apple2Keyboard Keyboard, Apple2Speaker Speaker,
    MachineHost Host, Apple2DiskII Disk, string Drive1Label)
{
    public static Apple2Surface Create(byte[] systemRom, byte[]? diskBootRom, byte[]? charRom,
                                       Action<byte[]> frameSink, Action<byte[]> audioSink,
                                       IFluxImage? drive1Image = null,
                                       ExecutionTier tier = ExecutionTier.Interpreter,
                                       string drive1Label = "—")
    {
        var state = new Apple2VideoState();
        // The video chip is constructed over a placeholder space; Realize re-binds it to the built
        // machine's program bus (the SpectrumUla/Apple2Video Realize contract).
        var placeholder = new AddressSpace(AddressSpaceKind.Program, 16);
        placeholder.MapMemory(0x0000, new byte[0x10000], writable: true);
        var video = new Apple2Video(placeholder, state, charRom);
        var keyboard = new Apple2Keyboard(state);
        var speaker = new Apple2Speaker(state);
        var lc = new Apple2LanguageCard(systemRom);
        var disk = new Apple2DiskII(drive1Image ?? new SyntheticFluxImage(trackCount: 35));
        var iou = new Apple2Iou(state, lc, disk);

        BoardSpec spec = diskBootRom is not null
            ? Apple2Board.SpecWithSystem(systemRom, iou, disk, diskBootRom)
            : Apple2Board.SpecWithDiskII(systemRom, iou, disk);

        Machine machine = BoardMachineFactory.Build(spec, tier);
        // The video/speaker chips are not board peripherals (the IOU owns $C000); Realize them over the
        // built machine so the video binds the live program bus + both schedule their 60 Hz ticks.
        // `Machine : IMachineContext` (verified: src/CpuEmulator.Core/Machine.cs `public sealed class
        // Machine : IMachineContext`), so the built machine IS the context — pass it directly.
        video.Realize(machine);
        speaker.Realize(machine);
        machine.Reset();

        var host = new MachineHost(machine, video, keyboard, frameSink, speaker, audioSink);
        var surface = new Apple2Surface(machine, video, keyboard, speaker, host, disk, drive1Label);
        surface._labels.Set(1, drive1Label);   // drive 1 starts at the ctor label ("—" for the plain ][+)
        return surface;
    }

    /// <summary>Composes the Apple ][+ running Apple Pascal (UCSD p-System) for the web surface (PR #153 board,
    /// reused via <see cref="Pascal.CreateBoard"/>): APPLE1 (boot) in drive 1, APPLE0 (program) in drive 2,
    /// re-nibblized at <see cref="Pascal.Order"/>, the Language Card in read-ROM/write-RAM mode. Boots to the
    /// interactive UCSD p-System COMMAND: line in the browser. Same video/keyboard/speaker triad as
    /// <see cref="Create"/>.</summary>
    public static Apple2Surface CreatePascal(byte[] systemRom, byte[] diskBootRom, byte[]? charRom,
                                             string bootDiskPath, string? programDiskPath,
                                             Action<byte[]> frameSink, Action<byte[]> audioSink)
    {
        // The canonical Pascal board (single source of truth — PascalBootTests + BootProbe share it). Build it,
        // then Realize the video/speaker over its machine + Reset, EXACTLY as Create does for the plain ][+.
        PascalBoard board = Pascal.CreateBoard(systemRom, diskBootRom, bootDiskPath, programDiskPath);

        // The video chip is constructed over a placeholder space; Realize re-binds it to the built machine's
        // program bus (the Apple2Video Realize contract) — the same placeholder pattern as Create.
        var placeholder = new AddressSpace(AddressSpaceKind.Program, 16);
        placeholder.MapMemory(0x0000, new byte[0x10000], writable: true);
        var video = new Apple2Video(placeholder, board.State, charRom);
        var keyboard = new Apple2Keyboard(board.State);
        var speaker = new Apple2Speaker(board.State);

        video.Realize(board.Machine);
        speaker.Realize(board.Machine);
        board.Machine.Reset();

        var host = new MachineHost(board.Machine, video, keyboard, frameSink, speaker, audioSink);
        var surface = new Apple2Surface(board.Machine, video, keyboard, speaker, host, board.Disk, "APPLE1");
        surface._labels.Set(1, "APPLE1");   // drive 1 = the boot volume
        surface._labels.Set(2, "APPLE0");   // drive 2 = the program/compiler volume
        return surface;
    }

    // Mutable per-drive labels for the ST frame (design D9/D14): the immutable record can't hold runtime
    // label state, so a tiny holder tracks each drive's current image label, updated on insert/eject.
    private readonly DriveLabels _labels = new();

    /// <summary>Snapshot the REAL machine state for the <c>ST</c> status frame (design D14): the board
    /// name, the live video-mode label, and the live per-drive motor + image label. Both modeled drives
    /// (PR-Q made drive 2 real) report the shared motor line + their tracked label.</summary>
    public MachineStatus Status() => new(
        Board: "Apple ][+",
        Asset: "apple",
        Mode: Video.ModeLabel,
        Drives:
        [
            new DriveStatus(Disk.MotorOn, _labels.Label1),
            new DriveStatus(Disk.MotorOn, _labels.Label2),
        ]);

    /// <summary>Insert a disk image (raw bytes + format) into <paramref name="drive"/> at runtime — the
    /// in-session swap the library (R) and upload (S) paths call (design T-D / D11–D12). Builds the
    /// IFluxImage via DiskImageFactory, hands it to the live Disk II controller, and tracks the per-drive
    /// label for the ST frame.</summary>
    public void InsertDisk(int drive, byte[] bytes, DiskFormat format, string label)
    {
        Disk.Insert(drive, DiskImageFactory.FromBytes(bytes, format));
        _labels.Set(drive, label);
    }

    /// <summary>PR-Q's two-arg overload (label defaults to "—") — kept so existing call sites/tests are
    /// unchanged.</summary>
    public void InsertDisk(int drive, byte[] bytes, DiskFormat format) =>
        InsertDisk(drive, bytes, format, "—");

    /// <summary>Eject <paramref name="drive"/>'s image at runtime (design D13 — allowed mid-access, no
    /// confirm). The drive reads nothing until a re-insert; its label returns to "—".</summary>
    public void EjectDisk(int drive)
    {
        Disk.Eject(drive);
        _labels.Set(drive, "—");
    }
}
