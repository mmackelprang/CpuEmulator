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
        return new Apple2Surface(machine, video, keyboard, speaker, host, disk, drive1Label);
    }

    /// <summary>Snapshot the REAL machine state for the <c>ST</c> status frame (design D14): the board
    /// name, the live video-mode label, and the live per-drive motor + image label. The plain ][+ has one
    /// modeled drive (drive 1; PR-F models drive 1) — the synthetic-image label is "—" until a real disk
    /// is inserted.</summary>
    public MachineStatus Status() => new(
        Board: "Apple ][+",
        Asset: "apple",
        Mode: Video.ModeLabel,
        Drives: [new DriveStatus(Disk.MotorOn, Drive1Label)]);
}
